using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// An issue as a job sees it when it starts: fresh from GitBucket, and planned again. A job whose
/// type is not the decision any more (duplicate or late job) returns NotNeeded.
/// </summary>
public sealed record IssueSnapshot(RepoConfig Repo, GbIssue Issue, IReadOnlyList<GbComment> Comments, PlanDecision Decision);

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
