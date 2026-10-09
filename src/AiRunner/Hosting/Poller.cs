using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Jobs;

namespace AiRunner.Hosting;

/// <summary>
/// Polls GitBucket (no webhooks: GitBucket cannot reach the laptop), asks JobPlanner what
/// each issue and PR needs, and queues the jobs. It never changes anything in GitBucket.
/// For PRs it reads comments only where a review fix (@ai) is possible.
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
        foreach (var repo in config.Repos)
        {
            try
            {
                await PollRepoAsync(repo, cancellationToken);
            }
            // An HTTP timeout is also an OperationCanceledException, so only a real shutdown is rethrown.
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError("{Repo}: poll failed: {Message}", repo, exception.Message);
            }
        }

        if (DryRun)
        {
            logger.LogInformation("Dry run: no job was queued.");
        }
    }

    private async Task PollRepoAsync(RepoConfig repo, CancellationToken cancellationToken)
    {
        // Scope guard: only issues with the scope label or assigned to the runner's user.
        var labelled = await gitBucketClient.ListOpenIssuesAsync(
            repo.FullName,
            config.ScopeLabel,
            null,
            cancellationToken);
        var assignedToRunner = await gitBucketClient.ListOpenIssuesAsync(
            repo.FullName,
            null,
            identity.Login,
            cancellationToken);

        // Filtered again in case the server ignores "assignee".
        var assigned = assignedToRunner.Where(issue => issue.IsAssignedTo(identity.Login));

        var issues = labelled.Concat(assigned)
            .DistinctBy(issue => issue.Number)
            .OrderBy(issue => issue.Number)
            .ToList();

        var seenFingerprints = stateStore.GetIssueFingerprints(repo.FullName);
        var fingerprints = issues.ToDictionary(issue => issue.Number, issue => issue.Fingerprint());
        var changedCount = issues.Count(
            issue => seenFingerprints.GetValueOrDefault(issue.Number) != fingerprints[issue.Number]);

        logger.LogInformation("{Repo}: {Total} open issues in scope, {Changed} to check", repo, issues.Count, changedCount);

        foreach (var issue in issues)
        {
            var isChanged = seenFingerprints.GetValueOrDefault(issue.Number) != fingerprints[issue.Number];

            // Labels decide most jobs; comments are read only when the analysis decision needs them.
            var planInput = new IssuePlanInput(repo, issue, Comments: null, LastHandledCommentId: 0);
            var decision = JobPlanner.PlanIssue(planInput);
            if (decision.NeedsComments && isChanged)
            {
                var comments = await gitBucketClient.ListCommentsAsync(repo.FullName, issue.Number, cancellationToken);
                var lastHandledComment = stateStore.GetLastHandledComment(repo.FullName, issue.Number);
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
                stateStore.SetIssueFingerprint(repo.FullName, issue.Number, fingerprints[issue.Number]);
            }
        }

        if (!repo.IsEnabled(Workflows.Review) && !repo.IsEnabled(Workflows.Fix))
        {
            return;
        }

        foreach (var pull in await gitBucketClient.ListPullsAsync(repo.FullName, "open", cancellationToken))
        {
            // Only PRs a review fix can touch need their comments; PRs have no fingerprint, so this is one call per poll each.
            var plannedPull = await jobLoader.PlanPullAsync(repo, pull, cancellationToken);
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
