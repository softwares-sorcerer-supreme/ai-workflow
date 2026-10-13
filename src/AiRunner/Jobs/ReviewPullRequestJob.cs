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
    IGitBucketClient gitBucketClient,
    JobLoader jobLoader,
    IWorkspace workspace,
    IGitClient git,
    ICopilotRunner copilot,
    IStateStore stateStore,
    AttachmentFetcher attachmentFetcher) : IJob
{
    public const string Agent = "pr-review";
    public const string Prompt =
        "Review the pull request described in context.md; the diff is in diff.patch. " +
        "Read components.md first: the other components are read-only. Follow the pr-review agent rules.";

    public JobType Type => JobType.ReviewPullRequest;

    public JobInfo Info
    {
        get
        {
            var timeout = TimeSpan.FromMinutes(config.Copilot.ReviewTimeoutMinutes + 10);
            return new(
                Workflows.Review,
                "AI review",
                timeout,
                OnPullRequest: true);
        }
    }

    /// <summary>Remember the failed commit, so it is not retried on every poll; the next commit is reviewed again.</summary>
    public void OnFailed(JobRequest request) =>
        stateStore.SetReview(request.Repo, request.Number, new ReviewState(request.Payload ?? "", null, "failed"));

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken cancellationToken)
    {
        // A review fix asked for since the poll goes first.
        var (project, pull, readComments, decision) = await jobLoader.LoadPullAsync(request, cancellationToken);
        if (decision.Job?.Type != Type)
        {
            return JobOutcome.NotNeeded(decision.Reason);
        }

        var previous = stateStore.GetReview(request.Repo, pull.Number);
        var sha = pull.Head.Sha;

        var pullHead = Branches.PullHead(pull.Number);
        var workspaceRequest = new WorkspaceRequest(
            Project: project,
            TargetRepo: request.Repo,
            StartPoint: sha,
            NewBranch: null,
            ExtraFetch: [pullHead]);
        var prepared = await workspace.PrepareAsync(workspaceRequest, cancellationToken);
        var directory = prepared.Root;
        var clone = prepared.TargetClone;
        var baseRef = Branches.Remote(pull.Base.Ref);
        var files = await git.ChangedFilesAsync(clone, baseRef, sha, cancellationToken);

        if (files.Count == 0 || PathGlob.AllMatch(files, project.Settings.Review.SkipPaths))
        {
            stateStore.SetReview(request.Repo, pull.Number, new ReviewState(sha, previous?.CommentId, "skipped"));
            return new JobOutcome(
                "skipped", files.Count == 0 ? "no changed files" : "only skipPaths files changed", Quiet: true);
        }

        var diff = await git.DiffAsync(clone, baseRef, sha, cancellationToken);
        var diffFile = Path.Combine(directory, "diff.patch");
        await File.WriteAllTextAsync(diffFile, diff, cancellationToken);
        var changedFilesFile = Path.Combine(directory, "changed-files.txt");
        await File.WriteAllLinesAsync(changedFilesFile, files, cancellationToken);

        var pullLink = new PullLink(project, request.Repo, pull);
        var linked = await gitBucketClient.LinkedIssueAsync(pullLink, run, cancellationToken);

        // The people's comments are part of the review input; the planner only read them when a fix was possible.
        var pullComments = readComments
            ?? await gitBucketClient.ListCommentsAsync(request.Repo, pull.Number, cancellationToken);
        var description = new AttachmentSource("the PR description", pull.Body);
        var attachmentRequest = AttachmentRequest.Of(directory, description, pullComments);
        var fetched = await attachmentFetcher.FetchAsync(attachmentRequest, run, cancellationToken);

        var humanComments = ContextBuilder.HumanComments(pullComments, "## Comments on the pull request (oldest first)");
        var context = ContextBuilder.ForPull(pull, files, linked) + humanComments + fetched.Section;
        var contextFile = Path.Combine(directory, "context.md");
        await File.WriteAllTextAsync(contextFile, context, cancellationToken);
        run.Save("context.md", context);
        run.Save("diff.patch", diff);

        var model = project.Settings.ModelFor(Workflows.Review);
        var timeout = TimeSpan.FromMinutes(config.Copilot.ReviewTimeoutMinutes);
        var output = await copilot.RunCopilotAsync(
            new CopilotRequest(directory, Prompt, Agent, model, timeout)
            {
                DenyTools = ["write", "shell"],
                Attachments = fetched.ForCopilot,
            }, run, cancellationToken);

        var review = JobSupport.RequireBlock(output, Markers.Review, run);
        var parsed = JobSupport.Parse(() => ReviewParser.Parse(review), "The review");

        // A newer commit arrived while Copilot was running: drop this review, the next poll reviews the new head.
        var current = await gitBucketClient.GetPullAsync(request.Repo, pull.Number, cancellationToken);
        if (current.Head.Sha != sha)
        {
            return new JobOutcome("outdated", $"head moved to {Text.ShortSha(current.Head.Sha)} during the review");
        }

        var body = Markers.Wrap(review, Markers.Review);
        var commentId = previous?.CommentId ?? BotComments.Latest(pullComments, Markers.Review)?.Id;

        GbComment posted;
        if (commentId is not null)
        {
            try
            {
                posted = await gitBucketClient.UpdateCommentAsync(
                    request.Repo, commentId.Value, body, cancellationToken);
            }
            catch (GitBucketException)
            {
                posted = await gitBucketClient.AddCommentAsync(request.Repo, pull.Number, body, cancellationToken);
            }
        }
        else
        {
            posted = await gitBucketClient.AddCommentAsync(request.Repo, pull.Number, body, cancellationToken);
        }

        stateStore.SetReview(request.Repo, pull.Number, new ReviewState(sha, posted.Id, parsed.Verdict));

        var detail = $"{parsed.Verdict}: {parsed.Bugs} bug, {parsed.Risks} risk, {parsed.Style} style";
        run.Info(detail);
        return JobOutcome.Ok(detail);
    }
}
