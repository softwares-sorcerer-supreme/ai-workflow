using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// Workflow 3: one AI review comment per PR, updated in place on each new head commit.
/// A first pass for the human reviewer; it never blocks the merge.
/// </summary>
public sealed class ReviewPullRequestJob(
    RunnerConfig config,
    IGitBucketClient gitBucket,
    IWorkspace workspace,
    IGitClient git,
    ICopilotRunner copilot,
    IStateStore state,
    RunnerIdentity identity) : IJob
{
    public const string Agent = "pr-review";
    public const string Prompt =
        "Review the pull request described in context.md; the diff is in diff.patch. Follow the pr-review agent rules.";

    public JobType Type => JobType.ReviewPullRequest;

    public JobInfo Info => new(Workflows.Review, "AI review", TimeSpan.FromMinutes(config.Copilot.ReviewTimeoutMinutes + 10),
        OnPullRequest: true);

    /// <summary>Remember the failed commit, so it is not retried on every poll; the next commit is reviewed again.</summary>
    public void OnFailed(JobRequest request) =>
        state.SetReview(request.Repo, request.Number, new ReviewState(request.Payload ?? "", null, "failed"));

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken ct)
    {
        var repo = config.RepoConfig(request.Repo);
        var pull = await gitBucket.GetPullAsync(request.Repo, request.Number, ct);
        var previous = state.GetReview(request.Repo, pull.Number);

        var decision = JobPlanner.PlanPull(repo, pull, previous?.HeadSha, identity.Login, config.ScopeLabel);
        if (decision.Job is null)
            return JobOutcome.NotNeeded(decision.Reason);
        var sha = pull.Head.Sha;

        var dir = await workspace.PrepareAsync(request.Repo, sha, null,
            [$"+refs/pull/{pull.Number}/head:refs/remotes/pr/{pull.Number}"], ct);
        var baseRef = Branches.Remote(pull.Base.Ref);
        var files = await git.ChangedFilesAsync(dir, baseRef, sha, ct);

        if (files.Count == 0 || PathGlob.AllMatch(files, repo.Review.SkipPaths))
        {
            state.SetReview(request.Repo, pull.Number, new ReviewState(sha, previous?.CommentId, "skipped"));
            return new JobOutcome("skipped", files.Count == 0 ? "no changed files" : "only skipPaths files changed", Quiet: true);
        }

        var diff = await git.DiffAsync(dir, baseRef, sha, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "diff.patch"), diff, ct);
        await File.WriteAllLinesAsync(Path.Combine(dir, "changed-files.txt"), files, ct);

        var linked = ContextBuilder.LinkedIssue(pull.Body);
        string? analysis = null, spec = null;
        if (linked is not null)
        {
            try
            {
                var issueComments = await gitBucket.ListCommentsAsync(request.Repo, linked.Value, ct);
                analysis = JobSupport.LatestBlock(issueComments, Markers.Analysis);
                spec = JobSupport.LatestBlock(issueComments, Markers.Spec);
                if (spec is null)
                {
                    // A sub-issue has no SPEC comment: its body (scope, touches, shared contract) is its plan.
                    var issue = await gitBucket.GetIssueAsync(request.Repo, linked.Value, ct);
                    if (issue.HasLabel(Labels.SubIssue)) spec = ContextBuilder.PlanFromSubIssue(issue);
                }
            }
            catch (GitBucketException ex)
            {
                run.Info($"linked issue #{linked} not readable: {ex.Message}");
            }
        }
        var context = ContextBuilder.ForPull(pull, files, linked, analysis, spec);
        await File.WriteAllTextAsync(Path.Combine(dir, "context.md"), context, ct);
        run.Save("context.md", context);
        run.Save("diff.patch", diff);

        var output = await copilot.RunCopilotAsync(
            new CopilotRequest(dir, Prompt, Agent, repo.ModelFor(Workflows.Review),
                TimeSpan.FromMinutes(config.Copilot.ReviewTimeoutMinutes))
            {
                DenyTools = ["write", "shell"],
            }, run, ct);

        var review = JobSupport.RequireBlock(output, Markers.Review, run);
        var parsed = JobSupport.Parse(() => ReviewParser.Parse(review), "The review");

        // A newer commit arrived while Copilot was running: drop this review, the next poll reviews the new head.
        var current = await gitBucket.GetPullAsync(request.Repo, pull.Number, ct);
        if (current.Head.Sha != sha)
            return new JobOutcome("outdated", $"head moved to {Text.ShortSha(current.Head.Sha)} during the review");

        var body = Markers.Wrap(review, Markers.Review);
        var commentId = previous?.CommentId
            ?? BotComments.Latest(await gitBucket.ListCommentsAsync(request.Repo, pull.Number, ct), Markers.Review)?.Id;
        GbComment posted;
        if (commentId is not null)
        {
            try { posted = await gitBucket.UpdateCommentAsync(request.Repo, commentId.Value, body, ct); }
            catch (GitBucketException) { posted = await gitBucket.AddCommentAsync(request.Repo, pull.Number, body, ct); }
        }
        else posted = await gitBucket.AddCommentAsync(request.Repo, pull.Number, body, ct);

        state.SetReview(request.Repo, pull.Number, new ReviewState(sha, posted.Id, parsed.Verdict));
        var detail = $"{parsed.Verdict}: {parsed.Bugs} bug, {parsed.Risks} risk, {parsed.Style} style";
        run.Info(detail);
        return JobOutcome.Ok(detail);
    }
}
