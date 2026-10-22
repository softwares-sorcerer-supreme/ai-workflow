using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Jobs;

/// <summary>
/// Workflow 3: one AI review comment per PR, updated in place on each new head commit. The first review reads the
/// whole PR, later ones only the commits since the last review (and check its findings again), up to
/// limits.max_reviews; the label ai-review-full asks for one more full review. A first pass for the human reviewer;
/// it never blocks the merge.
/// </summary>
public sealed class ReviewPullRequestJob(
    RunnerConfig config,
    ICodeHost codeHost,
    JobLoader jobLoader,
    IWorkspace workspace,
    IGitClient git,
    ICopilotRunner copilot,
    IStateStore stateStore,
    AttachmentFetcher attachmentFetcher) : IJob
{
    public const string Agent = "pr-review";
    public const string Prompt =
        JobSupport.RulesPrefix +
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
        stateStore.SetReview(request.Repo, request.Number, new ReviewState(request.Payload ?? "", null, ReviewState.Failed));

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
        var limits = project.Settings.Limits;

        // The people's comments are part of the review input; the newest REVIEW comment also carries the review
        // history in its tag, for when ai-runner.db was deleted. The planner only read them when a fix was possible.
        var pullComments = readComments
            ?? await codeHost.ListCommentsAsync(request.Repo, pull.Number, cancellationToken);
        var reviewComment = BotComments.Latest(pullComments, Markers.Review);
        var stored = previous?.ReviewCount is { } storedCount
            ? new ReviewHistory(previous.ReviewedSha, storedCount)
            : null;
        var history = ReviewHistory.Of(stored, reviewComment?.Body);
        var isAskedByLabel = JobPlanner.IsFullReviewAsked(pull, previous?.FullReviewSha);

        if (isAskedByLabel)
        {
            // Stored and removed before Copilot runs: a failed review or a label that stays does not ask again.
            var askedState = new ReviewState(previous?.HeadSha ?? sha, null, previous?.Result ?? ReviewState.Pending)
            {
                FullReviewSha = sha,
            };
            stateStore.SetReview(request.Repo, pull.Number, askedState);
            await codeHost.RemoveLabelAsync(request.Repo, pull.Number, Labels.ReviewFull, cancellationToken);
        }

        var workspaceRequest = new WorkspaceRequest(
            Project: project,
            TargetRepo: request.Repo,
            StartPoint: sha,
            NewBranch: null,
            PullHead: pull.Number)
        {
            OthersAt = Branches.FeatureBranchOf(pull),
        };
        run.Step(JobSteps.Prepare, "Prepare workspace");
        var prepared = await workspace.PrepareAsync(workspaceRequest, cancellationToken);
        var directory = prepared.Root;
        var clone = prepared.TargetClone;
        var baseRef = Branches.Remote(pull.Base.Ref);
        var pullFiles = await git.ChangedFilesAsync(clone, baseRef, sha, cancellationToken);

        // A later review reads only the commits since the last one; files the PR does not change came from merging
        // the base branch in and are left out.
        string? sinceSha = null;
        if (!isAskedByLabel)
        {
            sinceSha = await IncrementalBaseAsync(clone, history.ReviewedSha, sha, cancellationToken);
        }

        var files = pullFiles;
        if (sinceSha is not null)
        {
            var newFiles = await git.ChangedFilesAsync(clone, sinceSha, sha, cancellationToken);
            files = newFiles
                .Where(pullFiles.Contains)
                .ToList();
        }

        if (files.Count == 0 || PathGlob.AllMatch(files, project.Settings.Review.SkipPaths))
        {
            stateStore.SetReview(request.Repo, pull.Number, new ReviewState(sha, previous?.CommentId, ReviewState.Skipped));
            return new JobOutcome(
                "skipped", files.Count == 0 ? "no changed files" : "only skipPaths files changed", Quiet: true);
        }

        var rawDiff = await git.DiffAsync(clone, sinceSha ?? baseRef, sha, cancellationToken);
        var diff = sinceSha is null ? rawDiff : DiffFiles.Only(rawDiff, files);
        var diffFile = Path.Combine(directory, "diff.patch");
        await File.WriteAllTextAsync(diffFile, diff, cancellationToken);
        var changedFilesFile = Path.Combine(directory, "changed-files.txt");
        await File.WriteAllLinesAsync(changedFilesFile, files, cancellationToken);

        var pullLink = new PullLink(project, request.Repo, pull);
        var linked = await codeHost.LinkedIssueAsync(pullLink, run, cancellationToken);

        run.Step(JobSteps.Attachments, "Attachments");
        var description = new AttachmentSource("the PR description", pull.Body);
        var attachmentRequest = AttachmentRequest.Of(directory, description, pullComments);
        var fetched = await attachmentFetcher.FetchAsync(attachmentRequest, run, cancellationToken);

        string? previousReview = null;
        if (sinceSha is not null && reviewComment?.Body is { } reviewBody)
        {
            previousReview = Markers.Extract(reviewBody, Markers.Review);
        }

        var scopeInput = new ReviewScopeInput(sinceSha, history.Count + 1, limits.MaxReviews, previousReview)
        {
            IsAskedByLabel = isAskedByLabel,
        };
        var reviewScope = ContextBuilder.ReviewScope(scopeInput);
        var humanComments = ContextBuilder.HumanComments(pullComments, "## Comments on the pull request (oldest first)");
        var context = ContextBuilder.ForPull(pull, files, linked) + reviewScope + humanComments + fetched.Section;
        var contextFile = Path.Combine(directory, "context.md");
        await File.WriteAllTextAsync(contextFile, context, cancellationToken);
        run.Save("context.md", context);
        run.Save("diff.patch", diff);

        var modelContext = linked.Repo is { } linkedRepo && linked.Number is { } linkedNumber
            ? await codeHost.ReadAsync(new IssueRef(linkedRepo, linkedNumber), Workflows.Review, run, cancellationToken)
            : ModelContext.None;
        var modelInput = new ModelInput(
            project.Settings.ModelSettingFor(Workflows.Review),
            modelContext.IssueOverride,
            modelContext.Complexity,
            IsFixRound: false);
        var modelChoice = ModelResolver.Resolve(modelInput);
        run.RecordModel(modelChoice);
        var model = modelChoice.Model;
        var timeout = TimeSpan.FromMinutes(config.Copilot.ReviewTimeoutMinutes);
        run.Step(JobSteps.Copilot, "Copilot");
        var output = await copilot.RunCopilotAsync(
            new CopilotRequest(directory, Prompt, Agent, model, timeout)
            {
                DenyTools = ["write", "shell"],
                Attachments = fetched.ForCopilot,
            }, run, cancellationToken);

        var review = JobSupport.RequireBlock(output, Markers.Review, run);
        var parsed = JobSupport.Parse(() => ReviewParser.Parse(review), "The review");

        // A newer commit arrived while Copilot was running: drop this review, the next poll reviews the new head.
        var current = await codeHost.GetPullAsync(request.Repo, pull.Number, cancellationToken);
        if (current.Head.Sha != sha)
        {
            return new JobOutcome(
                "outdated", $"head moved to {Text.ShortSha(current.Head.Sha)} during the review", CopilotCalls: 1);
        }

        // A review asked for by label does not count; the one that uses the last automatic review says so.
        var newCount = isAskedByLabel ? history.Count : history.Count + 1;
        var newHistory = new ReviewHistory(sha, newCount);
        var limitNote = isAskedByLabel ? null : ReviewHistory.LimitNote(newCount, limits.MaxReviews);
        var wrapped = Markers.Wrap(review, Markers.Review);
        var body = wrapped + (limitNote is null ? "" : "\n\n" + limitNote) + "\n" + newHistory.Tag();
        var commentId = previous?.CommentId ?? reviewComment?.Id;

        run.Step(JobSteps.Comment, "Post review");
        HostComment posted;
        if (commentId is not null)
        {
            try
            {
                posted = await codeHost.UpdateCommentAsync(
                    request.Repo, commentId.Value, body, cancellationToken);
            }
            catch (CodeHostException)
            {
                posted = await codeHost.AddCommentAsync(request.Repo, pull.Number, body, cancellationToken);
            }
        }
        else
        {
            posted = await codeHost.AddCommentAsync(request.Repo, pull.Number, body, cancellationToken);
        }

        var reviewedState = new ReviewState(sha, posted.Id, parsed.Verdict)
        {
            ReviewedSha = sha,
            ReviewCount = newCount,
        };
        stateStore.SetReview(request.Repo, pull.Number, reviewedState);

        var scope = sinceSha is null ? "full" : "incremental";
        var detail =
            $"{parsed.Verdict} ({scope}, review {newCount} of {limits.MaxReviews}): {parsed.Bugs} bug, " +
            $"{parsed.Risks} risk, {parsed.Conventions} convention, {parsed.Style} style, " +
            $"{parsed.StillOpen} still open";
        run.Info(detail);
        return JobOutcome.Ok(detail, copilotCalls: 1);
    }

    /// <summary>
    /// The head of the last review when the next review can read only the commits after it: the commit still exists
    /// and is part of the head's history (no force push). Null means a full review.
    /// </summary>
    private async Task<string?> IncrementalBaseAsync(
        string clone,
        string? reviewedSha,
        string headSha,
        CancellationToken cancellationToken)
    {
        if (reviewedSha is null || string.Equals(reviewedSha, headSha, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var resolved = await git.ResolveAsync(clone, reviewedSha, cancellationToken);
        if (resolved is null)
        {
            return null;
        }

        // Commits of the reviewed head that the new head does not have: none means it is an ancestor.
        var commitsNotInHead = await git.CountCommitsAsync(clone, headSha, reviewedSha, cancellationToken);

        return commitsNotInHead == 0 ? reviewedSha : null;
    }
}
