using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Web;

/// <summary>A PR of an issue. <c>State</c> is <c>open</c>, <c>merged</c> or <c>closed</c>.</summary>
public sealed record PullResponse(
    string Repo,
    int Number,
    string Title,
    string State,
    string StateLabel,
    string HeadRef,
    string BaseRef,
    string? HtmlUrl,
    DateTimeOffset UpdatedAt);

/// <summary>What the analysis decided, read from the labels it set. Codes are <c>none</c> until it has run.</summary>
public sealed record AnalysisSummaryResponse(
    CodeLabelResponse Status,
    CodeLabelResponse Complexity,
    string? Component);

/// <summary>A sub-issue on the page of its parent: its row (stage, flags, badge) and its PRs.</summary>
public sealed record SubIssueDetailResponse(IssueRowResponse Issue, IReadOnlyList<PullResponse> Pulls);

/// <summary>
/// <c>GET /api/issues/{owner}/{repo}/{number}</c>. <c>Timeline</c> holds the jobs on the issue and on its PRs, newest
/// first; each job's log is read through <c>/api/jobs/{id}/files</c>. <c>Waiting</c> is null when nobody must act.
/// </summary>
public sealed record IssueDetailResponse(
    IssueRowResponse Issue,
    CodeLabelResponse? Waiting,
    AnalysisSummaryResponse Analysis,
    IReadOnlyList<SubIssueDetailResponse> SubIssues,
    IReadOnlyList<PullResponse> Pulls,
    IReadOnlyList<JobResponse> Timeline);

/// <summary>Builds the detail page of one issue from the issue board, the snapshot's PRs and the job table.</summary>
public sealed class IssueDetailReader(RunnerConfig config, IssueBoardReader boardReader, IJobReader jobs)
{
    private const string None = "none";

    /// <summary>The detail of "owner/repo#number", or null when the snapshot has no such issue.</summary>
    public IssueDetailResponse? Read(string repo, int number)
    {
        var board = boardReader.Read();
        var key = IssueBoard.KeyOf(repo, number);

        if (!board.Entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        var row = IssueReader.RowOf(entry);
        var waiting = entry.Waiting is { } action ? CodeLabelResponse.From(action) : null;
        var pulls = PullsOf(entry, board);
        var subIssues = entry.SubIssues
            .Where(subIssue => board.Entries.ContainsKey(subIssue.Key))
            .Select(subIssue => board.Entries[subIssue.Key])
            .Select(subEntry => new SubIssueDetailResponse(IssueReader.RowOf(subEntry), PullsOf(subEntry, board)))
            .ToList();
        var timeline = TimelineOf(entry, pulls);

        return new IssueDetailResponse(row, waiting, AnalysisOf(entry), subIssues, pulls, timeline);
    }

    private static AnalysisSummaryResponse AnalysisOf(IssueEntry entry)
    {
        var issue = entry.Issue;

        var status = issue switch
        {
            _ when issue.HasLabel(Labels.QuestionsOpen) => new CodeLabelResponse("questions-open", "Questions open"),
            _ when issue.HasLabel(Labels.Ready) => new CodeLabelResponse("ready", "Ready"),
            _ => new CodeLabelResponse(None, "Not analysed yet"),
        };
        var complexity = issue switch
        {
            _ when issue.HasLabel(Labels.Complex) => new CodeLabelResponse("complex", "Complex"),
            _ when issue.HasLabel(Labels.Simple) => new CodeLabelResponse("simple", "Simple"),
            _ => new CodeLabelResponse(None, "Unknown"),
        };

        return new AnalysisSummaryResponse(status, complexity, SnapshotRules.ComponentOf(issue, entry.Project));
    }

    /// <summary>
    /// The PRs of the issue: the ones the stage counts as "its PR" (found by branch) and any PR whose body links the
    /// issue. A PR of another project's repo never matches.
    /// </summary>
    private static List<PullResponse> PullsOf(IssueEntry entry, IssueBoard board)
    {
        var repos = entry.Project.CodeRepos.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return board.Pulls
            .Where(pull => repos.Contains(pull.Repo) && IsOfIssue(pull, entry))
            .OrderByDescending(pull => pull.UpdatedAt)
            .Select(PullOf)
            .ToList();
    }

    private static bool IsOfIssue(PullSnapshot pull, IssueEntry entry)
    {
        if (IssueBoard.KeyComparer.Equals(pull.LinkedIssue, entry.Issue.Key))
        {
            return true;
        }

        return entry.Stage.Pulls.Any(itsPull => itsPull.Number == pull.Number && itsPull.Head.Ref == pull.HeadRef);
    }

    private static PullResponse PullOf(PullSnapshot pull)
    {
        var (state, label) = pull switch
        {
            { IsMerged: true } => ("merged", "Merged"),
            { State: SnapshotRules.Open } => ("open", "Open"),
            _ => ("closed", "Closed"),
        };

        return new PullResponse(
            pull.Repo,
            pull.Number,
            pull.Title,
            state,
            label,
            pull.HeadRef,
            pull.BaseRef,
            pull.HtmlUrl,
            pull.UpdatedAt);
    }

    /// <summary>The jobs on the issue and on its PRs (reviews and fixes), newest first.</summary>
    private List<JobResponse> TimelineOf(IssueEntry entry, IReadOnlyList<PullResponse> pulls)
    {
        var records = new List<JobRecord>(jobs.ListForItem(entry.Issue.Repo, entry.Issue.Number));
        foreach (var pull in pulls)
        {
            records.AddRange(jobs.ListForItem(pull.Repo, pull.Number));
        }

        return records
            .DistinctBy(job => job.Id)
            .OrderByDescending(job => job.Id)
            .Select(job => JobResponse.From(job, config))
            .ToList();
    }
}
