using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

/// <summary>
/// Polls GitBucket (no webhooks: GitBucket cannot reach the laptop), asks JobPlanner what
/// each issue and PR needs, and queues the jobs. It never changes anything in GitBucket.
/// GitBucket ignores "since", so for each issue the poller remembers a fingerprint (updated_at +
/// labels) of the last time there was nothing to do, and reads comments only when it changed.
/// While an issue still needs a job, nothing is remembered: if the job fails without being able
/// to report it (e.g. VPN down), the next poll plans it again instead of losing the request.
/// </summary>
public sealed class Poller(
    RunnerConfig config,
    IGitBucketClient gitBucket,
    IStateStore state,
    JobQueue queue,
    RunnerIdentity identity,
    ILogger<Poller> log) : BackgroundService
{
    /// <summary>Print the decisions only; queue nothing and keep the local state unchanged.</summary>
    public bool DryRun { get; set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.PollIntervalSeconds));
        do
        {
            await PollOnceAsync(ct);
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        foreach (var repo in config.Repos)
        {
            try { await PollRepoAsync(repo, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) // an HTTP timeout is also an OperationCanceledException
            {
                log.LogError("{Repo}: poll failed: {Message}", repo, ex.Message);
            }
        }
        if (DryRun) log.LogInformation("Dry run: no job was queued.");
    }

    private async Task PollRepoAsync(RepoConfig repo, CancellationToken ct)
    {
        // Scope guard: only issues with the scope label or assigned to the runner's user.
        var labelled = await gitBucket.ListOpenIssuesAsync(repo.FullName, config.ScopeLabel, null, ct);
        var assigned = (await gitBucket.ListOpenIssuesAsync(repo.FullName, null, identity.Login, ct))
            .Where(i => i.IsAssignedTo(identity.Login)); // in case the server ignores "assignee"
        var issues = labelled.Concat(assigned).DistinctBy(i => i.Number).OrderBy(i => i.Number).ToList();

        var seen = state.GetIssueFingerprints(repo.FullName);
        var fingerprints = issues.ToDictionary(i => i.Number, i => i.Fingerprint());
        var changed = issues.Count(i => seen.GetValueOrDefault(i.Number) != fingerprints[i.Number]);
        log.LogInformation("{Repo}: {Total} open issues in scope, {Changed} to check", repo, issues.Count, changed);

        foreach (var issue in issues)
        {
            var isChanged = seen.GetValueOrDefault(issue.Number) != fingerprints[issue.Number];
            // Labels decide most jobs; comments are read only when the analysis decision needs them.
            var decision = JobPlanner.PlanIssue(repo, issue, null, 0);
            if (decision.NeedsComments && isChanged)
                decision = JobPlanner.PlanIssue(repo, issue, await gitBucket.ListCommentsAsync(repo.FullName, issue.Number, ct),
                    state.GetLastHandledComment(repo.FullName, issue.Number));
            Handle(decision, $"issue #{issue.Number} \"{issue.Title}\" [{string.Join(", ", issue.LabelNames())}]");
            if (!DryRun && decision.Job is null && isChanged)
                state.SetIssueFingerprint(repo.FullName, issue.Number, fingerprints[issue.Number]);
        }

        if (!repo.IsEnabled(Workflows.Review)) return;
        foreach (var pull in await gitBucket.ListPullsAsync(repo.FullName, "open", ct))
        {
            var decision = JobPlanner.PlanPull(repo, pull, state.GetReview(repo.FullName, pull.Number)?.HeadSha,
                identity.Login, config.ScopeLabel);
            Handle(decision, $"PR #{pull.Number} \"{pull.Title}\" {pull.Head.Ref} -> {pull.Base.Ref} @ {Text.ShortSha(pull.Head.Sha)}");
        }
    }

    private void Handle(PlanDecision decision, string what)
    {
        if (DryRun)
        {
            log.LogInformation("  {What} -> {Decision}", what, decision);
            return;
        }
        if (decision.Job is null)
        {
            log.LogDebug("  {What} -> {Decision}", what, decision);
            return;
        }
        var added = queue.Enqueue(decision.Job);
        var level = added && !decision.Routine ? LogLevel.Information : LogLevel.Debug;
        log.Log(level, "  {What} -> {Decision}{Queued}", what, decision, added ? " queued" : " already queued");
    }
}
