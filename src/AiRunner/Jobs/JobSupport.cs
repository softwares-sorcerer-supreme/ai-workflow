using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>Labels to put on an issue and labels to take off it, in one step.</summary>
public sealed record LabelChange(IEnumerable<string> Add, IEnumerable<string> Remove)
{
    public static LabelChange Adding(params string[] labels) => new(labels, []);

    public static LabelChange Removing(params string[] labels) => new([], labels);
}

/// <summary>Small steps several jobs share. No decisions here; those live in Core.</summary>
public static class JobSupport
{
    public static RepoConfig RepoConfig(this RunnerConfig config, string repo) =>
        config.FindRepo(repo) ?? throw new JobFailedException($"{repo} is not in repos.yml");

    /// <summary>
    /// Adds the labels that are missing and removes the ones that are present. Reads the issue
    /// again first: labels change during a job, and a stale copy would skip a removal.
    /// </summary>
    public static async Task ApplyLabelsAsync(
        this IGitBucketClient gitBucketClient,
        string repo,
        int number,
        LabelChange change,
        CancellationToken cancellationToken)
    {
        var issue = await gitBucketClient.GetIssueAsync(repo, number, cancellationToken);
        var current = issue.LabelNames();
        var missing = change.Add.Where(labelName => !current.Contains(labelName)).Distinct();
        await gitBucketClient.AddLabelsAsync(repo, number, missing, cancellationToken);

        foreach (var label in change.Remove.Where(current.Contains).Distinct())
        {
            await gitBucketClient.RemoveLabelAsync(repo, number, label, cancellationToken);
        }
    }

    public static async Task<string> DefaultBranchAsync(
        this IGitBucketClient gitBucketClient, string repo, CancellationToken cancellationToken) =>
        (await gitBucketClient.GetRepoAsync(repo, cancellationToken)).DefaultBranch
        ?? throw new JobFailedException($"GitBucket did not return the default branch of {repo}");

    /// <summary>
    /// The issue a PR links to ("Closes #12") with its final analysis and SPEC. A sub-issue has no SPEC
    /// comment: its body (scope, touches, shared contract) is its plan and comes back as the SPEC.
    /// An unreadable issue is logged, not fatal: the PR can still be handled without it.
    /// </summary>
    public static async Task<LinkedIssue> LinkedIssueAsync(this IGitBucketClient gitBucketClient, string repo,
        GbPull pull, JobRun run, CancellationToken cancellationToken)
    {
        var linked = ContextBuilder.LinkedIssue(pull.Body);
        if (linked is null)
        {
            return new LinkedIssue(null, null, null);
        }

        try
        {
            var comments = await gitBucketClient.ListCommentsAsync(repo, linked.Value, cancellationToken);
            var analysis = LatestBlock(comments, Markers.Analysis);
            var spec = LatestBlock(comments, Markers.Spec);
            if (spec is null)
            {
                var issue = await gitBucketClient.GetIssueAsync(repo, linked.Value, cancellationToken);
                if (issue.HasLabel(Labels.SubIssue))
                {
                    spec = ContextBuilder.PlanFromSubIssue(issue);
                }
            }

            return new LinkedIssue(linked, analysis, spec);
        }
        catch (GitBucketException exception)
        {
            run.Info($"linked issue #{linked} not readable: {exception.Message}");
            return new LinkedIssue(linked, null, null);
        }
    }

    /// <summary>The block inside the newest bot comment of that kind (ANALYSIS, SPEC, REVIEW), or null.</summary>
    public static string? LatestBlock(IEnumerable<GbComment> comments, string marker) =>
        BotComments.Latest(comments, marker) is { } comment ? Markers.Extract(comment.Body!, marker) : null;

    /// <summary>
    /// Runs Copilot and fails the job with a clear message when it did not finish well. The output is
    /// saved as {logName}-stdout.txt and {logName}-stderr.txt (a job with several runs names each one).
    /// </summary>
    public static async Task<string> RunCopilotAsync(this ICopilotRunner copilot, CopilotRequest request, JobRun run,
        CancellationToken cancellationToken, string logName = "copilot")
    {
        var result = await copilot.RunAsync(request, cancellationToken);
        run.Save($"{logName}-stdout.txt", result.StdOut);
        run.Save($"{logName}-stderr.txt", result.StdErr);
        run.Info($"copilot exit {result.ExitCode} after {result.Duration.TotalSeconds:F0}s");

        if (result.TimedOut)
        {
            throw new JobFailedException($"Copilot did not finish within {request.Timeout.TotalMinutes:F0} minutes.");
        }

        if (!result.Ok && CopilotRunner.LooksLikeQuotaError(result))
        {
            throw new JobFailedException("Copilot stopped: the premium requests of the seat look used up. " +
                                         $"Wait for the quota to reset, then remove {Labels.Failed} to retry.");
        }

        if (!result.Ok)
        {
            throw new JobFailedException($"Copilot exited with code {result.ExitCode}: {Text.Short(result.StdErr)}");
        }

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
        try
        {
            return parse();
        }
        catch (FormatException exception)
        {
            throw new JobFailedException($"{what} has the wrong format: {exception.Message}");
        }
    }
}
