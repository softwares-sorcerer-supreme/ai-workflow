using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Web;

/// <summary>
/// A PR of an issue. <c>State</c> is <c>open</c>, <c>merged</c> or <c>closed</c>. <c>Actions</c> are what the user can
/// ask for on this PR now (full review, fix); empty for a PR that is not open or not in scope.
/// </summary>
public sealed record PullResponse(
    string Repo,
    int Number,
    string Title,
    string State,
    string StateLabel,
    string HeadRef,
    string BaseRef,
    string? HtmlUrl,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ActionResponse> Actions);

/// <summary>What the analysis decided, read from the labels it set. Codes are <c>none</c> until it has run.</summary>
public sealed record AnalysisSummaryResponse(
    CodeLabelResponse Status,
    CodeLabelResponse Complexity,
    string? Component);

/// <summary>
/// <c>GET /api/issues/{owner}/{repo}/{number}</c>. The issue's row already holds what it waits on and its PRs; each
/// sub-issue is its own row (stage, flags, badge, PRs). <c>Timeline</c> holds the jobs on the issue and on its PRs,
/// newest first; each job's log is read through <c>/api/jobs/{id}/files</c>.
/// </summary>
public sealed record IssueDetailResponse(
    IssueRowResponse Issue,
    AnalysisSummaryResponse Analysis,
    IReadOnlyList<IssueRowResponse> SubIssues,
    IReadOnlyList<JobResponse> Timeline);

/// <summary>Builds the detail page of one issue from the issue board, the snapshot's PRs and the job table.</summary>
public sealed class IssueDetailReader(RunnerConfig config, IssueBoardReader boardReader, IJobReader jobs)
{
    /// <summary>The detail of "owner/repo#number", or null when the snapshot has no such issue.</summary>
    public IssueDetailResponse? Read(string repo, int number)
    {
        var board = boardReader.Read();
        var key = IssueBoard.KeyOf(repo, number);

        if (!board.Entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        var row = IssueReader.RowOf(entry, board);
        var subIssues = entry.SubIssues
            .Select(subIssue => board.Entries.GetValueOrDefault(subIssue.Key))
            .OfType<IssueEntry>()
            .Select(subEntry => IssueReader.RowOf(subEntry, board))
            .ToList();
        var timeline = TimelineOf(entry, row.Pulls);
        var analysis = AnalysisOf(entry);

        return new IssueDetailResponse(row, analysis, subIssues, timeline);
    }

    private static AnalysisSummaryResponse AnalysisOf(IssueEntry entry)
    {
        var summary = AnalysisSummary.Of(entry.Issue);
        var component = SnapshotRules.ComponentOf(entry.Issue, entry.Project);

        return new AnalysisSummaryResponse(
            CodeLabelResponse.From(summary.Status),
            CodeLabelResponse.From(summary.Complexity),
            component);
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
