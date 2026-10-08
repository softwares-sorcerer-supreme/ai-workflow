using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// An issue as a job sees it when it starts: fresh from GitBucket, and planned again. A job whose
/// type is not the decision any more (duplicate or late job) returns NotNeeded.
/// </summary>
public sealed record IssueSnapshot(RepoConfig Repo, GbIssue Issue, IReadOnlyList<GbComment> Comments, PlanDecision Decision);

/// <summary>A PR as a job sees it when it starts; Comments is null when the decision did not need them.</summary>
public sealed record PullSnapshot(RepoConfig Repo, GbPull Pull, IReadOnlyList<GbComment>? Comments, PlanDecision Decision);

/// <summary>The issue a PR links to, or Number null. Analysis and Spec are null when not found.</summary>
public sealed record LinkedIssue(int? Number, string? Analysis, string? Spec);

/// <summary>Small steps several jobs share. No decisions here; those live in Core.</summary>
public static class JobSupport
{
    public static RepoConfig RepoConfig(this RunnerConfig config, string repo) =>
        config.FindRepo(repo) ?? throw new JobFailedException($"{repo} is not in repos.yml");

    /// <summary>Reads the issue and its comments and asks JobPlanner again, since things changed since the poll.</summary>
    public static async Task<IssueSnapshot> LoadIssueAsync(this IGitBucketClient gb, RunnerConfig config,
        IStateStore state, JobRequest request, CancellationToken ct)
    {
        var repo = config.RepoConfig(request.Repo);
        var issue = await gb.GetIssueAsync(request.Repo, request.Number, ct);
        var comments = await gb.ListCommentsAsync(request.Repo, request.Number, ct);
        var decision = JobPlanner.PlanIssue(repo, issue, comments, state.GetLastHandledComment(request.Repo, issue.Number));
        return new IssueSnapshot(repo, issue, comments, decision);
    }

    /// <summary>
    /// Plans a PR the way the poller and the PR jobs all do: comments are read only when the decision
    /// needs them (a review fix is possible), then the PR is planned again with them.
    /// </summary>
    public static async Task<PullSnapshot> PlanPullAsync(this IGitBucketClient gb, RunnerConfig config, IStateStore state,
        RunnerIdentity identity, RepoConfig repo, GbPull pull, CancellationToken ct)
    {
        var reviewed = state.GetReview(repo.FullName, pull.Number)?.HeadSha;
        var decision = JobPlanner.PlanPull(repo, pull, reviewed, identity.Login, config.ScopeLabel);
        if (!decision.NeedsComments) return new PullSnapshot(repo, pull, null, decision);
        var comments = await gb.ListCommentsAsync(repo.FullName, pull.Number, ct);
        return new PullSnapshot(repo, pull, comments, JobPlanner.PlanPull(repo, pull, reviewed, identity.Login, config.ScopeLabel,
            comments, state.GetLastHandledComment(repo.FullName, pull.Number)));
    }

    /// <summary>Reads the PR fresh and plans it again, like LoadIssueAsync for issues.</summary>
    public static async Task<PullSnapshot> LoadPullAsync(this IGitBucketClient gb, RunnerConfig config, IStateStore state,
        RunnerIdentity identity, JobRequest request, CancellationToken ct) =>
        await gb.PlanPullAsync(config, state, identity, config.RepoConfig(request.Repo),
            await gb.GetPullAsync(request.Repo, request.Number, ct), ct);

    /// <summary>
    /// Adds the labels that are missing and removes the ones that are present. Reads the issue
    /// again first: labels change during a job, and a stale copy would skip a removal.
    /// </summary>
    public static async Task ApplyLabelsAsync(this IGitBucketClient gb, string repo, int number,
        IEnumerable<string> add, IEnumerable<string> remove, CancellationToken ct)
    {
        var current = (await gb.GetIssueAsync(repo, number, ct)).LabelNames();
        await gb.AddLabelsAsync(repo, number, add.Where(l => !current.Contains(l)).Distinct(), ct);
        foreach (var label in remove.Where(current.Contains).Distinct())
            await gb.RemoveLabelAsync(repo, number, label, ct);
    }

    public static async Task<string> DefaultBranchAsync(this IGitBucketClient gb, string repo, CancellationToken ct) =>
        (await gb.GetRepoAsync(repo, ct)).DefaultBranch
        ?? throw new JobFailedException($"GitBucket did not return the default branch of {repo}");

    /// <summary>
    /// The issue a PR links to ("Closes #12") with its final analysis and SPEC. A sub-issue has no SPEC
    /// comment: its body (scope, touches, shared contract) is its plan and comes back as the SPEC.
    /// An unreadable issue is logged, not fatal: the PR can still be handled without it.
    /// </summary>
    public static async Task<LinkedIssue> LinkedIssueAsync(this IGitBucketClient gb, string repo, GbPull pull, JobRun run,
        CancellationToken ct)
    {
        var linked = ContextBuilder.LinkedIssue(pull.Body);
        if (linked is null) return new LinkedIssue(null, null, null);
        try
        {
            var comments = await gb.ListCommentsAsync(repo, linked.Value, ct);
            var analysis = LatestBlock(comments, Markers.Analysis);
            var spec = LatestBlock(comments, Markers.Spec);
            if (spec is null)
            {
                var issue = await gb.GetIssueAsync(repo, linked.Value, ct);
                if (issue.HasLabel(Labels.SubIssue)) spec = ContextBuilder.PlanFromSubIssue(issue);
            }
            return new LinkedIssue(linked, analysis, spec);
        }
        catch (GitBucketException ex)
        {
            run.Info($"linked issue #{linked} not readable: {ex.Message}");
            return new LinkedIssue(linked, null, null);
        }
    }

    /// <summary>The block inside the newest bot comment of that kind (ANALYSIS, SPEC, REVIEW), or null.</summary>
    public static string? LatestBlock(IEnumerable<GbComment> comments, string marker) =>
        BotComments.Latest(comments, marker) is { } c ? Markers.Extract(c.Body!, marker) : null;

    /// <summary>
    /// Runs Copilot and fails the job with a clear message when it did not finish well. The output is
    /// saved as {logName}-stdout.txt and {logName}-stderr.txt (a job with several runs names each one).
    /// </summary>
    public static async Task<string> RunCopilotAsync(this ICopilotRunner copilot, CopilotRequest request, JobRun run,
        CancellationToken ct, string logName = "copilot")
    {
        var result = await copilot.RunAsync(request, ct);
        run.Save($"{logName}-stdout.txt", result.StdOut);
        run.Save($"{logName}-stderr.txt", result.StdErr);
        run.Info($"copilot exit {result.ExitCode} after {result.Duration.TotalSeconds:F0}s");

        if (result.TimedOut)
            throw new JobFailedException($"Copilot did not finish within {request.Timeout.TotalMinutes:F0} minutes.");
        if (!result.Ok && CopilotRunner.LooksLikeQuotaError(result))
            throw new JobFailedException("Copilot stopped: the premium requests of the seat look used up. " +
                                         $"Wait for the quota to reset, then remove {Labels.Failed} to retry.");
        if (!result.Ok)
            throw new JobFailedException($"Copilot exited with code {result.ExitCode}: {Text.Short(result.StdErr)}");
        return result.StdOut;
    }

    /// <summary>The marked block from agent output; a missing block fails the job (never post raw output).</summary>
    public static string RequireBlock(string output, string marker, JobRun run)
    {
        var block = Markers.Extract(output, marker)
            ?? throw new JobFailedException($"No {marker} block in the Copilot output (markers missing).");
        run.Save($"{marker.ToLowerInvariant()}.md", block);
        return block;
    }

    /// <summary>Calls a Core parser; a format error becomes a job failure with the parser's message.</summary>
    public static T Parse<T>(Func<T> parse, string what)
    {
        try { return parse(); }
        catch (FormatException ex) { throw new JobFailedException($"{what} has the wrong format: {ex.Message}"); }
    }
}
