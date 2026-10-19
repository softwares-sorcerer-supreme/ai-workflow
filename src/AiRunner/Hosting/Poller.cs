using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Jobs;

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
/// </summary>
public sealed class Poller(
    RunnerConfig config,
    IGitBucketClient gitBucketClient,
    JobLoader jobLoader,
    IStateStore stateStore,
    JobQueue queue,
    RunnerIdentity identity,
    RunnerStatus status,
    ILogger<Poller> logger) : BackgroundService
{
    /// <summary>Print the decisions only; queue nothing and keep the local state unchanged.</summary>
    public bool DryRun { get; set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var pollInterval = TimeSpan.FromSeconds(config.PollIntervalSeconds);
        using var timer = new PeriodicTimer(pollInterval);

        do
        {
            await PollOnceAsync(cancellationToken);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        status.PollStarted();

        foreach (var project in config.Projects)
        {
            // The first error of this poll is what the dashboard shows for the project.
            string? failure = null;

            try
            {
                await PollIssuesAsync(project, cancellationToken);
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
                    await PollPullsAsync(new PullLocation(project, repo), cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogError("{Repo}: PR poll failed: {Message}", repo, exception.Message);
                    failure ??= $"{repo}: PR poll failed: {exception.Message}";
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

    private async Task PollIssuesAsync(ProjectConfig project, CancellationToken cancellationToken)
    {
        // All issues of a project live in its issues repo; issues anywhere else are ignored.
        var repo = project.IssuesRepo;

        // Scope guard: only issues with the trigger label or assigned to the runner's user.
        var labelled = await gitBucketClient.ListOpenIssuesAsync(
            repo,
            project.Settings.TriggerLabel,
            null,
            cancellationToken);
        var assignedToRunner = await gitBucketClient.ListOpenIssuesAsync(
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

        foreach (var issue in issues)
        {
            var isChanged = seenFingerprints.GetValueOrDefault(issue.Number) != fingerprints[issue.Number];

            // Labels decide most jobs; comments are read only when the analysis decision needs them.
            var planInput = new IssuePlanInput(project, repo, issue, Comments: null, LastHandledCommentId: 0);
            var decision = JobPlanner.PlanIssue(planInput);
            if (decision.NeedsComments && isChanged)
            {
                var comments = await gitBucketClient.ListCommentsAsync(repo, issue.Number, cancellationToken);
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
    }

    private async Task PollPullsAsync(PullLocation location, CancellationToken cancellationToken)
    {
        var (project, repo) = location;
        var settings = project.Settings;

        if (!settings.IsEnabled(Workflows.Review) && !settings.IsEnabled(Workflows.Fix))
        {
            return;
        }

        foreach (var pull in await gitBucketClient.ListPullsAsync(repo, "open", cancellationToken))
        {
            // Only PRs a review fix can touch need their comments; PRs have no fingerprint, so this is one call per poll each.
            var plannedPull = await jobLoader.PlanPullAsync(location, pull, cancellationToken);
            var decision = plannedPull.Decision;
            var shortSha = Text.ShortSha(pull.Head.Sha);

            Handle(decision, $"PR #{pull.Number} \"{pull.Title}\" {pull.Head.Ref} -> {pull.Base.Ref} @ {shortSha}");
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
}
