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
    IReadOnlyDictionary<string, IReadOnlyList<GbPull>> PullsByRepo,
    IReadOnlyList<PullSnapshot> Pulls)
{
    /// <summary>"owner/repo#n" keys compare like repo names: case-insensitive.</summary>
    public static StringComparer KeyComparer { get; } = StringComparer.OrdinalIgnoreCase;

    public static string KeyOf(string repo, int number) => new IssueRef(repo, number).ToString();
}

/// <summary>Reads the snapshot, the job table and the running job, and asks Core for every issue's stage and action.</summary>
public sealed class IssueBoardReader(RunnerConfig config, ISnapshotReader snapshot, IJobReader jobs, RunnerStatus status)
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
        var stages = new Dictionary<string, StageResult>(comparer);
        var projects = new Dictionary<string, ProjectConfig>(comparer);
        foreach (var issue in issues)
        {
            var issueProject = config.ProjectNamed(issue.Project);

            // A project no longer in repos.yml is not polled any more: its old rows stay in the database, unshown.
            if (issueProject is null)
            {
                continue;
            }

            var claimHolder = comparer.Equals(issue.Key, claimedKey) ? runnerName : null;
            var input = new StageInput(issueProject, issue, subIssuesOf[issue.Key].ToList(), pullsByRepo)
            {
                Jobs = ActivityOf(jobsOf[issue.Key], claimHolder),
            };

            stages[issue.Key] = IssueStage.Compute(input);
            projects[issue.Key] = issueProject;
        }

        var entries = new Dictionary<string, IssueEntry>(comparer);
        foreach (var issue in issues.Where(issue => stages.ContainsKey(issue.Key)))
        {
            var subIssues = subIssuesOf[issue.Key].ToList();
            var stage = stages[issue.Key];
            var progress = subIssues
                .Select(subIssue => ProgressOf(subIssue, stages))
                .ToList();
            var autoStart = projects[issue.Key].Settings.AutoStartFirstSubIssue;
            var waiting = WaitingOn.Of(new WaitingInput(issue, stage, autoStart, progress));

            entries[issue.Key] = new IssueEntry(projects[issue.Key], issue, subIssues, stage, waiting);
        }

        return new IssueBoard(entries, pullsByRepo, pulls);
    }

    private static SubIssueProgress ProgressOf(IssueSnapshot subIssue, Dictionary<string, StageResult> stages)
    {
        if (!stages.TryGetValue(subIssue.Key, out var stage))
        {
            return new SubIssueProgress(subIssue.IsOpen, IssueStage.New, false);
        }

        return new SubIssueProgress(subIssue.IsOpen, stage.Stage.Code, stage.Badge is not null);
    }

    /// <summary>The issue or PR this runner's running job holds the claim on, or null.</summary>
    private string? ClaimedKey()
    {
        var running = status.Snapshot().Running;

        return running is { HoldsClaim: true } ? IssueBoard.KeyOf(running.Repo, running.Number) : null;
    }

    private static Dictionary<string, IReadOnlyList<GbPull>> PullsByRepo(IReadOnlyList<PullSnapshot> pulls) =>
        pulls
            .GroupBy(pull => pull.Repo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GbPull>)group.Select(pull => pull.AsPull()).ToList(),
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
