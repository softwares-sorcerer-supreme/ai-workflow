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
/// Each poll first applies a changed repos.yml (<see cref="RunnerConfigSource.ReloadIfChanged"/>), then works in its own
/// DI scope, so the whole poll sees one config.
/// </summary>
public sealed class Poller(
    RunnerConfigSource configSource,
    IServiceScopeFactory scopes,
    ICodeHost codeHost,
    IStateStore stateStore,
    JobQueue queue,
    RunnerIdentity identity,
    RunnerCredentials credentials,
    RunnerStatus status,
    RunnerCommands commands,
    AgentFiles agentFiles,
    ILogger<Poller> logger) : BackgroundService
{
    private int _pollCount;

    /// <summary>Projects whose slow snapshot refresh is due; one stays due until its refresh went through.</summary>
    private readonly HashSet<string> _refreshDue = [];

    /// <summary>Print the decisions only; queue nothing and keep the local state unchanged.</summary>
    public bool DryRun { get; set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var pollInterval = TimeSpan.FromSeconds(configSource.Current.PollIntervalSeconds);
        using var timer = new PeriodicTimer(pollInterval);

        // The wait that lost is kept for the next round: PeriodicTimer allows one wait in flight, and a second read
        // of the wake channel would swallow the next wake. A wake during a poll completes the kept wait, so it gives
        // exactly one more poll after it.
        Task<bool>? tick = null;
        Task? wake = null;
        while (true)
        {
            // Setup mode: no code-host call until the Setup page saves a token (or after it removed the last one).
            await credentials.WaitForTokenAsync(cancellationToken);
            await PollOnceAsync(cancellationToken);

            // poll_interval_seconds is live: the next wait already uses a changed value.
            var currentInterval = TimeSpan.FromSeconds(configSource.Current.PollIntervalSeconds);
            if (timer.Period != currentInterval)
            {
                timer.Period = currentInterval;
            }

            tick ??= timer.WaitForNextTickAsync(cancellationToken).AsTask();
            wake ??= commands.WaitForWakeAsync(cancellationToken);
            var completed = await Task.WhenAny(tick, wake);

            // Rethrows a shutdown.
            await completed;

            if (completed == tick)
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

        // A hand edit is applied here; a broken one keeps the working config and shows on the dashboard.
        var reload = configSource.ReloadIfChanged();
        logger.LogReload(reload);

        // The first resolution pins the scope's config: every project, list and plan of this poll uses it.
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var config = services.GetRequiredService<RunnerConfig>();
        var jobLoader = services.GetRequiredService<JobLoader>();
        var snapshot = services.GetRequiredService<SnapshotRecorder>();
        var tools = new PollTools(jobLoader, snapshot);

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
                openIssues = await PollIssuesAsync(project, tools, cancellationToken);
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
                    var location = new PullLocation(project, repo);
                    openPulls[repo] = await PollPullsAsync(location, tools, cancellationToken);
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

        RecordAgentDrift();
        status.PollEnded();

        if (DryRun)
        {
            logger.LogInformation("Dry run: no job was queued.");
        }
    }

    /// <summary>Plans every open issue in scope; returns them for the snapshot.</summary>
    private async Task<IReadOnlyList<HostIssue>> PollIssuesAsync(
        ProjectConfig project,
        PollTools tools,
        CancellationToken cancellationToken)
    {
        var snapshot = tools.Snapshot;

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
        PollTools tools,
        CancellationToken cancellationToken)
    {
        var (jobLoader, snapshot) = tools;
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

    /// <summary>
    /// Compares ~/.copilot/ with agents/ and skills/ for the health strip, so a git pull of this repo while the runner
    /// runs is noticed. Local file reads only; a read error is a warning, never a failed poll.
    /// </summary>
    private void RecordAgentDrift()
    {
        try
        {
            var drift = agentFiles.CheckDrift();
            status.AgentDriftChecked(drift.IsOutOfSync);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not compare the agents with ~/.copilot: {Error}", exception.Message);
        }
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

    /// <summary>The scoped services of one poll (they read the poll's config).</summary>
    private sealed record PollTools(JobLoader JobLoader, SnapshotRecorder Snapshot);
}
