using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Hosting;

/// <summary>
/// Polls GitBucket (no webhooks: GitBucket cannot reach the laptop), asks JobPlanner what
/// each issue and PR needs, and queues the jobs. It never changes anything in GitBucket.
/// Issues are read from each project's issues repo only; PRs from every repo that holds code.
/// For PRs it reads comments only where a review fix (the trigger keyword) is possible.
/// GitBucket ignores "since", so for each issue the poller remembers a fingerprint (updated_at +
/// labels) of the last time there was nothing to do, and reads comments only when it changed.
/// While an issue still needs a job, nothing is remembered: if the job fails without being able
/// to report it (e.g. VPN down), the next poll plans it again instead of losing the request.
/// What it lists also goes to the dashboard's snapshot (<see cref="SnapshotRecorder"/>), which never
/// changes a decision; open PRs are listed for it even when review and fix are off.
/// </summary>
public sealed class Poller(
    RunnerConfig config,
    ICodeHost codeHost,
    JobLoader jobLoader,
    IStateStore stateStore,
    JobQueue queue,
    RunnerIdentity identity,
    RunnerStatus status,
    SnapshotRecorder snapshot,
    RunnerCommands commands,
    ILogger<Poller> logger) : BackgroundService
{
    private int _pollCount;

    /// <summary>Projects whose slow snapshot refresh is due; one stays due until its refresh went through.</summary>
    private readonly HashSet<string> _refreshDue = [];

    /// <summary>Print the decisions only; queue nothing and keep the local state unchanged.</summary>
    public bool DryRun { get; set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var pollInterval = TimeSpan.FromSeconds(config.PollIntervalSeconds);
        using var timer = new PeriodicTimer(pollInterval);

        // The wait that lost is kept for the next round: PeriodicTimer allows one wait in flight, and a second read
        // of the wake channel would swallow the next wake. A wake during a poll completes the kept wait, so it gives
        // exactly one more poll after it.
        Task<bool>? tick = null;
        Task? wake = null;
        while (true)
        {
            await PollOnceAsync(cancellationToken);

            tick ??= timer.WaitForNextTickAsync(cancellationToken).AsTask();
            wake ??= commands.WaitForWakeAsync(cancellationToken);
            var first = await Task.WhenAny(tick, wake);

            // Rethrows a shutdown.
            await first;

            if (first == tick)
            {
                tick = null;
            }
            else
            {
                wake = null;
            }
        }
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        status.PollStarted();

        _pollCount++;
        if (SnapshotRecorder.IsRefreshDue(_pollCount))
        {
            _refreshDue.UnionWith(config.Projects.Select(project => project.Name));
        }

        foreach (var project in config.Projects)
        {
            // The first error of this poll is what the dashboard shows for the project.
            string? failure = null;
            IReadOnlyList<HostIssue> openIssues = [];
            var openPulls = new Dictionary<string, IReadOnlyList<HostPull>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                openIssues = await PollIssuesAsync(project, cancellationToken);
            }
            // An HTTP timeout is also an OperationCanceledException, so only a real shutdown is rethrown.
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError("{Project}: issue poll failed: {Message}", project, exception.Message);
                failure = $"issue poll failed: {exception.Message}";
            }

            foreach (var repo in project.CodeRepos)
            {
                try
                {
                    openPulls[repo] = await PollPullsAsync(new PullLocation(project, repo), cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogError("{Repo}: PR poll failed: {Message}", repo, exception.Message);
                    failure ??= $"{repo}: PR poll failed: {exception.Message}";
                }
            }

            // Both need complete lists (a missing item is read as gone): after a failed list they wait for the next poll.
            if (!DryRun && failure is null)
            {
                var poll = new ProjectPoll(project, openIssues, openPulls);
                await snapshot.RefreshVanishedAsync(poll, cancellationToken);

                if (_refreshDue.Contains(project.Name) && await snapshot.RefreshClosedAsync(poll, cancellationToken))
                {
                    _refreshDue.Remove(project.Name);
                }
            }

            if (failure is null)
            {
                status.ProjectPolled(project.Name);
            }
            else
            {
                status.ProjectFailed(project.Name, failure);
            }
        }

        status.PollEnded();

        if (DryRun)
        {
            logger.LogInformation("Dry run: no job was queued.");
        }
    }

    /// <summary>Plans every open issue in scope; returns them for the snapshot.</summary>
    private async Task<IReadOnlyList<HostIssue>> PollIssuesAsync(
        ProjectConfig project,
        CancellationToken cancellationToken)
    {
        // All issues of a project live in its issues repo; issues anywhere else are ignored.
        var repo = project.IssuesRepo;

        // Scope guard: only issues with the trigger label or assigned to the runner's user.
        var labelled = await codeHost.ListOpenIssuesAsync(
            repo,
            project.Settings.TriggerLabel,
            null,
            cancellationToken);
        var assignedToRunner = await codeHost.ListOpenIssuesAsync(
            repo,
            null,
            identity.Login,
            cancellationToken);

        // Filtered again in case the server ignores "assignee".
        var assigned = assignedToRunner.Where(issue => issue.IsAssignedTo(identity.Login));

        var issues = labelled.Concat(assigned)
            .DistinctBy(issue => issue.Number)
            .OrderBy(issue => issue.Number)
            .ToList();

        var seenFingerprints = stateStore.GetIssueFingerprints(repo);
        var fingerprints = issues.ToDictionary(issue => issue.Number, issue => issue.Fingerprint());
        var changedCount = issues.Count(
            issue => seenFingerprints.GetValueOrDefault(issue.Number) != fingerprints[issue.Number]);

        logger.LogInformation("{Repo}: {Total} open issues in scope, {Changed} to check", repo, issues.Count, changedCount);

        if (!DryRun)
        {
            snapshot.RecordIssues(project, issues);
        }

        foreach (var issue in issues)
        {
            var isChanged = seenFingerprints.GetValueOrDefault(issue.Number) != fingerprints[issue.Number];

            // Labels decide most jobs; comments are read only when the analysis decision needs them.
            var planInput = new IssuePlanInput(project, repo, issue, Comments: null, LastHandledCommentId: 0);
            var decision = JobPlanner.PlanIssue(planInput);
            if (decision.NeedsComments && isChanged)
            {
                var comments = await codeHost.ListCommentsAsync(repo, issue.Number, cancellationToken);
                var lastHandledComment = stateStore.GetLastHandledComment(repo, issue.Number);
                var planInputWithComments = planInput with
                {
                    Comments = comments,
                    LastHandledCommentId = lastHandledComment
                };
                decision = JobPlanner.PlanIssue(planInputWithComments);
            }

            Handle(decision, $"issue #{issue.Number} \"{issue.Title}\" [{string.Join(", ", issue.LabelNames())}]");

            if (!DryRun && decision.Job is null && isChanged)
            {
                stateStore.SetIssueFingerprint(repo, issue.Number, fingerprints[issue.Number]);
            }
        }

        return issues;
    }

    /// <summary>Plans the open PRs of one repo when review or fix is on; returns them for the snapshot.</summary>
    private async Task<IReadOnlyList<HostPull>> PollPullsAsync(
        PullLocation location,
        CancellationToken cancellationToken)
    {
        var (project, repo) = location;
        var settings = project.Settings;
        var isPlanned = settings.IsEnabled(Workflows.Review) || settings.IsEnabled(Workflows.Fix);

        // The list is needed for planning, or for the snapshot; a dry run writes no snapshot.
        if (!isPlanned && DryRun)
        {
            return [];
        }

        var pulls = await codeHost.ListPullsAsync(repo, "open", cancellationToken);

        if (!DryRun)
        {
            snapshot.RecordPulls(location, pulls);
        }

        if (!isPlanned)
        {
            return pulls;
        }

        foreach (var pull in pulls)
        {
            // Only PRs a review fix can touch need their comments; PRs have no fingerprint, so this is one call per poll each.
            var plannedPull = await jobLoader.PlanPullAsync(location, pull, cancellationToken);
            var decision = plannedPull.Decision;
            var shortSha = Text.ShortSha(pull.Head.Sha);

            Handle(decision, $"PR #{pull.Number} \"{pull.Title}\" {pull.Head.Ref} -> {pull.Base.Ref} @ {shortSha}");
        }

        return pulls;
    }

    private void Handle(PlanDecision decision, string what)
    {
        if (DryRun)
        {
            logger.LogInformation("  {What} -> {Decision}", what, decision);
            return;
        }

        if (decision.Job is null)
        {
            logger.LogDebug("  {What} -> {Decision}", what, decision);
            return;
        }

        var added = queue.Enqueue(decision.Job);
        var level = added && !decision.Routine ? LogLevel.Information : LogLevel.Debug;

        logger.Log(level, "  {What} -> {Decision}{Queued}", what, decision, added ? " queued" : " already queued");
    }
}
