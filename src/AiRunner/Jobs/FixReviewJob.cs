using System.Globalization;
using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// Review fix: someone wrote @ai on a PR in scope (labelled ai, or opened by the runner). Copilot
/// reads the latest AI review and the comments since the last fix, changes the code on the PR's
/// branch and goes through the same stages and runner checks as Workflow 2, then writes REPLY.md.
/// The runner adds a commit (never forced), pushes it and answers on the PR; the new head is then
/// reviewed again as usual. A fix that changes nothing still gets its reply.
/// </summary>
public sealed class FixReviewJob(
    RunnerConfig config,
    IGitBucketClient gitBucket,
    IWorkspace workspace,
    IGitClient git,
    AgentSession session,
    IStateStore state,
    RunnerIdentity identity) : IJob
{
    public const string Agent = "fix-review";
    // The stages and the follow-up procedure live only in agents/fix-review.agent.md.
    public const string Prompt =
        "Apply the review feedback in review-fix.md to this pull request. Follow the fix-review agent rules and AGENTS.md; finish every stage before you stop.";
    public const string FixPrompt =
        "Follow-up round: read fix.md and follow the Follow-up round section of the fix-review agent rules.";

    public JobType Type => JobType.FixReview;

    public JobInfo Info => new(Workflows.Fix, "AI review fix", AgentSession.JobTimeout(config), OnPullRequest: true,
        Retry: "Write a new comment with @ai to try again.");

    /// <summary>The @ai comment counts as answered, so a failure is not retried on every poll.</summary>
    public void OnFailed(JobRequest request)
    {
        if (long.TryParse(request.Payload, NumberStyles.None, CultureInfo.InvariantCulture, out var trigger))
            state.SetLastHandledComment(request.Repo, request.Number, trigger);
    }

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken ct)
    {
        var (repo, pull, readComments, decision) = await gitBucket.LoadPullAsync(config, state, identity, request, ct);
        if (decision.Job?.Type != Type)
            return JobOutcome.NotNeeded(decision.Reason);
        var comments = readComments!;   // a fix is only planned from comments
        var readUpTo = comments.Max(c => c.Id);
        var number = JobPlanner.NextFix(repo, comments).Number;

        // The PR head is fetched too (refs/pull), so a fork PR gets the refusal below instead of a git error.
        var sha = pull.Head.Sha;
        var dir = await workspace.PrepareAsync(request.Repo, sha, pull.Head.Ref, [Branches.PullHead(pull.Number)], ct);
        if (JobPlanner.WhyNotFix(repo, pull, comments, await gitBucket.DefaultBranchAsync(request.Repo, ct),
                await git.ResolveAsync(dir, Branches.Remote(pull.Head.Ref), ct)) is { } why)
            return await RefuseAsync(request.Repo, pull.Number, readUpTo, why, ct);

        var linked = await gitBucket.LinkedIssueAsync(request.Repo, pull, run, ct);
        var plan = ContextBuilder.PlanForReviewFix(linked.Number, linked.Spec);
        var review = JobSupport.LatestBlock(comments, Markers.Review);
        // Requests start after the last reply, not after the local last-handled id: a failed fix marks its
        // @ai comment handled, but its requests were never applied and must reach the next fix.
        var afterId = BotComments.AnsweredUpTo(comments, Markers.ReviewFix, 0);
        var context = ContextBuilder.ForReviewFix(pull, review, JobSupport.LatestBlock(comments, Markers.ReviewFix),
            comments.Where(c => c.Id > afterId), number);
        await File.WriteAllTextAsync(Path.Combine(dir, "plan.md"), plan, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "review-fix.md"), context, ct);
        run.Save("plan.md", plan);
        run.Save("review-fix.md", context);

        var task = new AgentTask(Agent, Workflows.Fix, FirstRound: "review fix", Prompt, FixPrompt, OutputFile: "REPLY.md",
            ReviewReplyParser.Problems, DiffBase: sha);
        var result = await session.RunAsync(task, request.Repo, repo, dir, run, ct);
        // Half-done work is not pushed onto a PR someone is reviewing.
        if (result.FirstFailure is { } failure)
            throw new JobFailedException(failure.Message + " Nothing was pushed.");

        IReadOnlyList<DiffStat>? stats;
        try
        {
            stats = await session.SaveWorkAsync(dir, $"PR #{pull.Number}: review fix {number} (Copilot)", pull.Head.Ref, run, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new JobFailedException($"Pushing to `{pull.Head.Ref}` failed, most likely because someone pushed to it " +
                                         $"while Copilot was working. Nothing was pushed. ({Text.Short(ex.Message, 300)})");
        }
        var commit = stats is null ? null : await git.ResolveAsync(dir, "HEAD", ct);

        await PostReplyAsync(request.Repo, pull.Number, readUpTo, Reply(number, pull.Head.Ref, stats?.Count ?? 0, commit, result), ct);
        var outcome = stats is null ? "no-change" : result.Failing ? "wip" : "ok";
        return new JobOutcome(outcome, $"fix {number}, {stats?.Count ?? 0} files, {result.Rounds.Count} copilot calls");
    }

    private static string Reply(int number, string branch, int changedFiles, string? commit, AgentResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(changedFiles == 0
            ? $"## AI fix {number} - no code change"
            : $"## AI fix {number} - commit {(commit is null ? "?" : Text.ShortSha(commit))}, {changedFiles} changed files on `{branch}`");
        sb.AppendLine();
        if (result.Failing)
            sb.AppendLine("**Build, test or lint is still failing after the fix.** See the check output below.").AppendLine();
        sb.AppendLine((result.OutputText ?? "(Copilot did not write REPLY.md.)").Trim()).AppendLine();
        AgentSession.AppendReport(sb, result, "REPLY.md problems");
        return sb.ToString();
    }

    /// <summary>Answers the @ai comment without running Copilot; the reply marks it handled.</summary>
    private async Task<JobOutcome> RefuseAsync(string repoName, int number, long readUpTo, string why, CancellationToken ct)
    {
        await PostReplyAsync(repoName, number, readUpTo, $"## AI fix - not started\n\n{why}", ct);
        return new JobOutcome("rejected", why);
    }

    /// <summary>The reply carries what this fix read, so later comments count as new even without the database.</summary>
    private async Task PostReplyAsync(string repoName, int number, long readUpTo, string reply, CancellationToken ct)
    {
        await gitBucket.AddCommentAsync(repoName, number, BotComments.WithUpto(reply, Markers.ReviewFix, readUpTo), ct);
        state.SetLastHandledComment(repoName, number, readUpTo);
    }
}
