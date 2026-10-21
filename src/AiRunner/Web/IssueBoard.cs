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
    CodeLabel? Waiting,
    IReadOnlyList<IssueAction> Actions);

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

    /// <summary>The runner's login on the code host: the PRs it opened itself are in the scope of the PR actions.</summary>
    public string BotLogin { get; init; } = "";

    /// <summary>"owner/repo#n" of every issue or PR this runner has a job queued or running for.</summary>
    public IReadOnlySet<string> BusyKeys { get; init; } = new HashSet<string>();

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
            .Select(pull => PullOf(pull, entry.Project.Settings))
            .ToList();
    }

    private PullResponse PullOf(PullSnapshot pull, ProjectSettings settings)
    {
        var (state, label) = pull switch
        {
            { IsMerged: true } => ("merged", "Merged"),
            { State: SnapshotRules.Open } => ("open", "Open"),
            _ => ("closed", "Closed"),
        };

        var hasActiveJob = BusyKeys.Contains(KeyOf(pull.Repo, pull.Number));
        var actionInput = new PullActionInput(pull.AsPull(), settings, BotLogin, hasActiveJob);
        var actions = ActionResponse.ListOf(IssueActions.AvailableForPull(actionInput));

        return new PullResponse(
            pull.Repo,
            pull.Number,
            pull.Title,
            state,
            label,
            pull.HeadRef,
            pull.BaseRef,
            pull.HtmlUrl,
            pull.UpdatedAt,
            actions);
    }
}

/// <summary>Reads the snapshot, the job table and the running job, and asks Core for every issue's stage and action.</summary>
public sealed class IssueBoardReader(
    RunnerConfig config,
    ISnapshotReader snapshot,
    IJobReader jobs,
    RunnerStatus status,
    RunnerIdentity identity)
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

        JobActivity ActivityOfIssue(IssueSnapshot issue)
        {
            var claimHolder = comparer.Equals(issue.Key, claimedKey) ? runnerName : null;

            return ActivityOf(jobsOf[issue.Key], claimHolder);
        }

        // In build order, so each parent's sub-issues come out in it.
        var subIssuesOf = issues
            .Where(issue => issue.Parent is not null)
            .OrderBy(issue => issue.SubOrder ?? 0)
            .ThenBy(issue => issue.Number)
            .ToLookup(issue => IssueBoard.KeyOf(issue.Repo, issue.Parent!.Value), comparer);

        var entries = new Dictionary<string, IssueEntry>(comparer);
        foreach (var issue in issues)
        {
            var issueProject = config.ProjectNamed(issue.Project);

            // A project no longer in repos.yml is not polled any more: its old rows stay in the database, unshown.
            if (issueProject is null)
            {
                continue;
            }

            var subIssues = subIssuesOf[issue.Key].ToList();
            var stagingInput = new StagingInput(issueProject, issue, subIssues, pullsByRepo, ActivityOfIssue);
            var staged = IssueStaging.Build(stagingInput);
            var actions = IssueActions.Available(staged.Actions);

            entries[issue.Key] = new IssueEntry(issueProject, issue, subIssues, staged.Stage, staged.Waiting, actions);
        }

        return new IssueBoard(entries, pulls)
        {
            BotLogin = identity.Login,
            BusyKeys = jobsOf.Select(group => group.Key).ToHashSet(comparer),
        };
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
