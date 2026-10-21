using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;

namespace AiRunner.Web;

/// <summary>One snapshot issue with everything the API says about it: stage, flags, badge and what it waits on.</summary>
public sealed record IssueEntry(
    ProjectConfig Project,
    IssueSnapshot Issue,
    IReadOnlyList<IssueSnapshot> SubIssues,
    StageResult Stage,
    CodeLabel? Waiting);

/// <summary>
/// The issues of the snapshot with their stage, computed once per request, so the list, the overview and the detail
/// page agree. <c>Entries</c> holds only issues of projects that are in repos.yml.
/// </summary>
public sealed record IssueBoard(
    IReadOnlyDictionary<string, IssueEntry> Entries,
    IReadOnlyList<PullSnapshot> Pulls)
{
    /// <summary>"owner/repo#n" keys compare like repo names: case-insensitive.</summary>
    public static StringComparer KeyComparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>Built once per board, so finding the PRs of every row is not one scan of all PRs per issue.</summary>
    private readonly ILookup<string, PullSnapshot> _pullsByLinkedIssue = Pulls
        .Where(pull => pull.LinkedIssue is not null)
        .ToLookup(pull => pull.LinkedIssue!, KeyComparer);

    private readonly ILookup<string, PullSnapshot> _pullsByHead = Pulls.ToLookup(
        pull => pull.HeadRef,
        StringComparer.Ordinal);

    public static string KeyOf(string repo, int number) => new IssueRef(repo, number).ToString();

    /// <summary>
    /// The PRs of an issue, newest first: the ones the stage counts as "its PR" (found by branch) and any PR whose
    /// body links the issue. A PR of another project's repo never matches.
    /// </summary>
    public IReadOnlyList<PullResponse> PullsOf(IssueEntry entry)
    {
        var repos = entry.Project.CodeRepos.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var linking = _pullsByLinkedIssue[entry.Issue.Key];
        var itsPulls = entry.Stage.Pulls.SelectMany(itsPull =>
            _pullsByHead[itsPull.Head.Ref].Where(pull => pull.Number == itsPull.Number));

        return linking
            .Concat(itsPulls)
            .Where(pull => repos.Contains(pull.Repo))
            .Distinct()
            .OrderByDescending(pull => pull.UpdatedAt)
            .Select(PullOf)
            .ToList();
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
}

/// <summary>Reads the snapshot, the job table and the running job, and asks Core for every issue's stage and action.</summary>
public sealed class IssueBoardReader(
    RunnerConfig config,
    ISnapshotReader snapshot,
    IJobReader jobs,
    RunnerStatus status)
{
    public IssueBoard Read(string? project = null)
    {
        var comparer = IssueBoard.KeyComparer;

        // Rows of another project are never shown, and sub-issues live in their parent's project.
        var issues = snapshot.ListIssues()
            .Where(issue => project is null || issue.Project == project)
            .ToList();
        var pulls = snapshot.ListPulls();
        var pullsByRepo = PullsByRepo(pulls);
        var jobsOf = jobs.ListActive().ToLookup(job => IssueBoard.KeyOf(job.Repo, job.Number), comparer);
        var claimedKey = ClaimedKey();
        var runnerName = config.EffectiveRunnerName();

        // In build order, so each parent's sub-issues come out in it.
        var subIssuesOf = issues
            .Where(issue => issue.Parent is not null)
            .OrderBy(issue => issue.SubOrder ?? 0)
            .ThenBy(issue => issue.Number)
            .ToLookup(issue => IssueBoard.KeyOf(issue.Repo, issue.Parent!.Value), comparer);

        // First every stage, then the actions: "start the next sub-issue" reads the stages of the sub-issues.
        var staged = new Dictionary<string, Staged>(comparer);
        foreach (var issue in issues)
        {
            var issueProject = config.ProjectNamed(issue.Project);

            // A project no longer in repos.yml is not polled any more: its old rows stay in the database, unshown.
            if (issueProject is null)
            {
                continue;
            }

            var subIssues = subIssuesOf[issue.Key].ToList();
            var claimHolder = comparer.Equals(issue.Key, claimedKey) ? runnerName : null;
            var input = new StageInput(issueProject, issue, subIssues, pullsByRepo)
            {
                Jobs = ActivityOf(jobsOf[issue.Key], claimHolder),
            };

            staged[issue.Key] = new Staged(issueProject, issue, subIssues, IssueStage.Compute(input));
        }

        var entries = new Dictionary<string, IssueEntry>(comparer);
        foreach (var (key, item) in staged)
        {
            var progress = item.SubIssues
                .Select(subIssue => ProgressOf(subIssue, staged))
                .ToList();
            var autoStart = item.Project.Settings.AutoStartFirstSubIssue;
            var waiting = WaitingOn.Of(new WaitingInput(item.Issue, item.Stage, autoStart, progress));

            entries[key] = new IssueEntry(item.Project, item.Issue, item.SubIssues, item.Stage, waiting);
        }

        return new IssueBoard(entries, pulls);
    }

    /// <summary>One issue after pass 1: its project, sub-issues and stage.</summary>
    private sealed record Staged(
        ProjectConfig Project,
        IssueSnapshot Issue,
        IReadOnlyList<IssueSnapshot> SubIssues,
        StageResult Stage);

    private static SubIssueProgress ProgressOf(IssueSnapshot subIssue, Dictionary<string, Staged> staged)
    {
        if (!staged.TryGetValue(subIssue.Key, out var item))
        {
            return new SubIssueProgress(subIssue.IsOpen, IssueStage.New, false);
        }

        return new SubIssueProgress(subIssue.IsOpen, item.Stage.Stage.Code, item.Stage.Badge is not null);
    }

    /// <summary>The issue or PR this runner's running job holds the claim on, or null.</summary>
    private string? ClaimedKey()
    {
        var running = status.Snapshot().Running;

        return running is { HoldsClaim: true } ? IssueBoard.KeyOf(running.Repo, running.Number) : null;
    }

    private static Dictionary<string, IReadOnlyList<HostPull>> PullsByRepo(IReadOnlyList<PullSnapshot> pulls) =>
        pulls
            .GroupBy(pull => pull.Repo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<HostPull>)group.Select(pull => pull.AsPull()).ToList(),
                StringComparer.OrdinalIgnoreCase);

    private static JobActivity ActivityOf(IEnumerable<JobRecord> jobsOfIssue, string? claimHolder)
    {
        var statuses = jobsOfIssue
            .Select(job => job.Status)
            .ToList();
        var isRunning = statuses.Contains(JobStatus.Running);
        var isQueued = statuses.Contains(JobStatus.Pending);

        return new JobActivity(isQueued, isRunning)
        {
            ClaimHolder = claimHolder,
        };
    }
}
