using System.Globalization;
using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>The @ai thread of a PR that a review fix answers.</summary>
public sealed record ReviewFixThread(string Repo, int Number, AtAiThread Thread);

/// <summary>What a review fix did, for the reply on the PR.</summary>
/// <param name="Branch">The branch the work is on: the PR's own branch, or a side branch when the checks are red.</param>
/// <param name="Commit">The commit that was pushed, or null when nothing was.</param>
/// <param name="SidePushError">Why saving on the side branch failed, or null.</param>
public sealed record ReviewFixResult(
    int Number,
    string Branch,
    int ChangedFiles,
    AgentResult Result,
    string? Commit = null,
    string? SidePushError = null);

/// <summary>
/// Review fix: someone wrote @ai on a PR in scope (labelled ai, or opened by the runner). It is Workflow 2
/// on an existing PR: the same agent, stages, runner checks and PR_BODY.md, with a plan.md that holds the
/// latest AI review and the comments since the last fix. A fix that passes build, test and lint is added as
/// a commit (never forced) to the PR's branch and answered on the PR; the new head is then reviewed again as
/// usual. A fix that is still red is never pushed to the PR: it is saved on a side branch. A fix that
/// changes nothing still gets its reply.
/// </summary>
public sealed class FixReviewJob(
    RunnerConfig config,
    IGitBucketClient gitBucketClient,
    JobLoader jobLoader,
    IWorkspace workspace,
    IGitClient git,
    AgentSession session,
    IStateStore stateStore,
    AttachmentFetcher attachmentFetcher) : IJob
{
    public JobType Type => JobType.FixReview;

    public JobInfo Info
    {
        get
        {
            var timeout = AgentSession.JobTimeout(config);
            return new(Workflows.Fix, "AI review fix", timeout, OnPullRequest: true,
                Retry: "Write a new comment with @ai to try again.");
        }
    }

    /// <summary>The @ai comment counts as answered, so a failure is not retried on every poll.</summary>
    public void OnFailed(JobRequest request)
    {
        if (long.TryParse(request.Payload, NumberStyles.None, CultureInfo.InvariantCulture, out var trigger))
        {
            stateStore.SetLastHandledComment(request.Repo, request.Number, trigger);
        }
    }

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken cancellationToken)
    {
        var (repo, pull, readComments, decision) = await jobLoader.LoadPullAsync(request, cancellationToken);
        if (decision.Job?.Type != Type)
        {
            return JobOutcome.NotNeeded(decision.Reason);
        }

        var comments = readComments!;   // a fix is only planned from comments

        // The local cursor only matters for Trigger, which the planner already used.
        var thread = AtAiThread.Of(comments, Markers.ReviewFix, 0);
        var number = thread.Next(repo.Limits.MaxReviewFixes).Number;
        var replyTarget = new ReviewFixThread(request.Repo, pull.Number, thread);

        // The PR head is fetched too (refs/pull), so a fork PR gets the refusal below instead of a git error.
        var sha = pull.Head.Sha;
        var pullHead = Branches.PullHead(pull.Number);
        var workspaceRequest = new WorkspaceRequest(
            Repo: request.Repo,
            StartPoint: sha,
            NewBranch: pull.Head.Ref,
            ExtraFetch: [pullHead]);
        var directory = await workspace.PrepareAsync(workspaceRequest, cancellationToken);
        var defaultBranch = await gitBucketClient.DefaultBranchAsync(request.Repo, cancellationToken);
        var remoteHead = Branches.Remote(pull.Head.Ref);
        var pushTarget = await git.ResolveAsync(directory, remoteHead, cancellationToken);
        var fixCheck = new FixCheckInput(repo, pull, comments, defaultBranch, pushTarget);
        var refusalReason = JobPlanner.WhyNotFix(fixCheck);
        if (refusalReason is not null)
        {
            return await RefuseAsync(replyTarget, refusalReason, cancellationToken);
        }

        var linked = await gitBucketClient.LinkedIssueAsync(request.Repo, pull, run, cancellationToken);
        var latestReview = JobSupport.LatestBlock(comments, Markers.Review);
        var lastReply = JobSupport.LatestBlock(comments, Markers.ReviewFix);
        var planInput = new ReviewFixPlanInput(
            Pull: pull,
            Review: latestReview,
            LastReply: lastReply,
            Requests: thread.RequestsSinceReply,
            FixNumber: number,
            Linked: linked);

        var description = new AttachmentSource("the PR description", pull.Body);
        var attachmentRequest = AttachmentRequest.Of(directory, description, thread.RequestsSinceReply);
        var fetched = await attachmentFetcher.FetchAsync(attachmentRequest, run, cancellationToken);

        var plan = ContextBuilder.PlanForReviewFix(planInput) + fetched.Section;
        var planFile = Path.Combine(directory, "plan.md");
        await File.WriteAllTextAsync(planFile, plan, cancellationToken);
        run.Save("plan.md", plan);

        // The criteria are the requests the agent lists itself; the linked SPEC's criteria must not be counted,
        // so no plan is passed.
        var task = new AgentTask(
            ImplementIssueJob.Agent,
            Workflows.Fix,
            FirstRound: "review fix",
            ImplementIssueJob.Prompt,
            ImplementIssueJob.FixPrompt,
            OutputFile: "PR_BODY.md",
            text => PrBodyParser.Problems(text, ""),
            DiffBase: sha)
        {
            Attachments = fetched.ForCopilot,
        };
        var context = new AgentRunContext(repo, directory, run);
        var result = await session.RunAsync(task, context, cancellationToken);

        // Half-done work is not pushed onto a PR someone is reviewing.
        if (result.FirstFailure is { } failure)
        {
            throw new JobFailedException(failure.Message + " Nothing was pushed.");
        }

        // Red code is never pushed onto a PR someone is reviewing;
        // it is kept on a side branch so the work is not lost.
        var target = result.Failing ? Branches.ReviewFix(pull.Number, number) : pull.Head.Ref;
        IReadOnlyList<DiffStat>? stats = null;
        string? sidePushError = null;
        try
        {
            var commitMessage = $"PR #{pull.Number}: review fix {number} (Copilot)";
            var work = new WorkToSave(directory, commitMessage, target);
            stats = await session.SaveWorkAsync(work, run, cancellationToken);
        }
        catch (InvalidOperationException exception) when (result.Failing)
        {
            // The PR itself is untouched, so the reply still goes out.
            sidePushError = Text.Short(exception.Message, 300);
        }
        catch (InvalidOperationException exception)
        {
            throw new JobFailedException($"Pushing to `{pull.Head.Ref}` failed, most likely because someone pushed to it " +
                                         $"while Copilot was working. Nothing was pushed. ({Text.Short(exception.Message, 300)})");
        }

        var changed = stats?.Count ?? 0;
        string reply;
        if (result.Failing)
        {
            var redFix = new ReviewFixResult(number, target, changed, result, SidePushError: sidePushError);
            reply = RedReply(redFix);
        }
        else
        {
            // The pushed commit is only looked up when something was pushed.
            var commit = stats is null ? null : await git.ResolveAsync(directory, "HEAD", cancellationToken);
            var pushedFix = new ReviewFixResult(number, pull.Head.Ref, changed, result, Commit: commit);
            reply = Reply(pushedFix);
        }

        await PostReplyAsync(replyTarget, reply, cancellationToken);

        var outcome = result.Failing ? "red" : stats is null ? "no-change" : "ok";
        return new JobOutcome(outcome, $"fix {number}, {changed} files, {result.Rounds.Count} copilot calls");
    }

    private static string Reply(ReviewFixResult fix)
    {
        var (number, branch, changedFiles, result, commit, _) = fix;
        var builder = new StringBuilder();
        builder.AppendLine(changedFiles == 0
            ? $"## AI fix {number} - no code change"
            : $"## AI fix {number} - commit {(commit is null ? "?" : Text.ShortSha(commit))}, " +
              $"{changedFiles} changed files on `{branch}`");
        builder.AppendLine();
        AppendOutput(builder, result);

        return builder.ToString();
    }

    /// <summary>Build, test or lint still failed after the fix rounds, so the fix is not on the PR.</summary>
    private static string RedReply(ReviewFixResult fix)
    {
        var (number, sideBranch, changedFiles, result, _, sidePushError) = fix;
        var builder = new StringBuilder();
        builder.AppendLine($"## AI fix {number} - not pushed to this PR: build, test or lint is still failing")
            .AppendLine();
        builder.AppendLine(sidePushError is not null
            ? $"I could not save the work on `{sideBranch}` either ({sidePushError}). Nothing was pushed."
            : changedFiles == 0
                ? "Copilot changed no file."
                : $"The {changedFiles} changed files are saved on branch `{sideBranch}`, not on this PR. " +
                  "Finish it by hand there, or write @ai again with more detail.").AppendLine();
        AppendOutput(builder, result);

        return builder.ToString();
    }

    private static void AppendOutput(StringBuilder builder, AgentResult result)
    {
        var outputText = (result.OutputText ?? "(Copilot did not write PR_BODY.md.)").Trim();
        builder.AppendLine(outputText).AppendLine();
        AgentSession.AppendReport(builder, result, "PR_BODY.md problems");
    }

    /// <summary>Answers the @ai comment without running Copilot; the reply marks it handled.</summary>
    private async Task<JobOutcome> RefuseAsync(
        ReviewFixThread target,
        string reason,
        CancellationToken cancellationToken)
    {
        var reply = $"## AI fix - not started\n\n{reason}";
        await PostReplyAsync(target, reply, cancellationToken);

        return new JobOutcome("rejected", reason);
    }

    /// <summary>The reply carries what this fix read, so later comments count as new even without the database.</summary>
    private async Task PostReplyAsync(ReviewFixThread target, string reply, CancellationToken cancellationToken)
    {
        var (repo, number, thread) = target;
        var commentBody = thread.Reply(reply);
        await gitBucketClient.AddCommentAsync(repo, number, commentBody, cancellationToken);
        stateStore.SetLastHandledComment(repo, number, thread.ReadUpTo);
    }
}
