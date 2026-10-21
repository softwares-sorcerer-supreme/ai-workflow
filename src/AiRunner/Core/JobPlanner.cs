using System.Globalization;
using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>
/// Decides which job an issue or PR needs, from its labels and comments (design doc
/// sections 5 and 10). Pure: the poller gives it what it read from GitBucket and enqueues
/// the result. The jobs ask it again on fresh data before doing anything.
/// </summary>
public static class JobPlanner
{
    public const string SkipAiPrefix = "[skip-ai]";

    public static PlanDecision PlanIssue(IssuePlanInput input)
    {
        var (project, repo, issue, comments, lastHandledCommentId) = input;
        var settings = project.Settings;
        var labels = issue.LabelNames();
        var createJob = (JobType type) => new JobRequest(type, repo, issue.Number);

        if (!issue.IsOpen())
        {
            return PlanDecision.Skip("closed");
        }

        if (labels.Contains(Labels.Failed))
        {
            return PlanDecision.Skip($"{Labels.Failed} - a human must read the log and remove the label to retry");
        }

        if (labels.Contains(Labels.NeedsCloudAgent) || labels.Contains(Labels.OnGitHub))
        {
            return PlanDecision.Skip("cloud fallback (Workflow 4) is not built yet");
        }

        if (labels.Contains(Labels.Implement))
        {
            if (!settings.IsEnabled(Workflows.Implement))
            {
                return PlanDecision.Skip("implement workflow is off for this project");
            }

            if (labels.Contains(Labels.Implementing))
            {
                return PlanDecision.Skip("already implementing");
            }

            // A refused request still becomes a job: the job explains why and removes the label.
            var implementJob = createJob(JobType.ImplementIssue);
            var implementRefusal = WhyNotImplement(labels);
            var implementReason = implementRefusal is null
                ? $"label {Labels.Implement}"
                : $"label {Labels.Implement}, will be refused: {implementRefusal}";
            return PlanDecision.Run(implementJob, implementReason);
        }

        if (labels.Contains(Labels.Breakdown))
        {
            if (!settings.IsEnabled(Workflows.Breakdown))
            {
                return PlanDecision.Skip("breakdown workflow is off for this project");
            }

            if (labels.Contains(Labels.Parent))
            {
                return PlanDecision.Skip("already broken down");
            }

            var breakdownJob = createJob(JobType.BreakdownIssue);
            var breakdownRefusal = WhyNotBreakdown(labels, settings.TriggerKeyword);
            var breakdownReason = breakdownRefusal is null
                ? $"label {Labels.Breakdown}"
                : $"label {Labels.Breakdown}, will be refused: {breakdownRefusal}";
            return PlanDecision.Run(breakdownJob, breakdownReason);
        }

        if (labels.Contains(Labels.Parent))
        {
            if (!settings.IsEnabled(Workflows.Breakdown))
            {
                return PlanDecision.Skip("breakdown workflow is off for this project");
            }

            var sequenceJob = createJob(JobType.SequenceSubIssues);
            var sequenceDecision = PlanDecision.Run(sequenceJob, "check sub-issue progress");

            return sequenceDecision with
            {
                Routine = true
            };
        }

        // A simple issue whose PR was opened: the job closes the issue once the PR is merged. Sub-issues are
        // closed by the sequencer of their parent.
        if (labels.Contains(Labels.Implementing) && !labels.Contains(Labels.SubIssue) &&
            settings.IsEnabled(Workflows.Implement))
        {
            var finishJob = createJob(JobType.FinishIssue);
            var finishDecision = PlanDecision.Run(finishJob, "check whether the PR is merged");

            return finishDecision with
            {
                Routine = true
            };
        }

        if (!settings.IsEnabled(Workflows.Analyze))
        {
            return PlanDecision.Skip("analyze workflow is off for this project");
        }

        if (comments is null)
        {
            return PlanDecision.Skip("comments not read (no change since last poll)") with
            {
                NeedsComments = true
            };
        }

        var analysisKind = new ThreadKind(Markers.Analysis, settings.TriggerKeyword);
        var thread = AtAiThread.Of(comments, analysisKind, lastHandledCommentId);
        var trigger = thread.Trigger;

        if (thread.Done == 0)
        {
            // Sub-issues come with their scope from the breakdown; they are analyzed only on request.
            if (labels.Contains(Labels.SubIssue) && trigger is null)
            {
                return PlanDecision.Skip($"sub-issue - write {settings.TriggerKeyword} to analyze it");
            }

            var firstRoundJob = createJob(JobType.AnalyzeIssue);
            return PlanDecision.Run(firstRoundJob, "round 1");
        }

        if (trigger is null)
        {
            return PlanDecision.Skip($"wait for a new comment with {settings.TriggerKeyword}");
        }

        var round = thread.Next(settings.Limits.MaxAnalysisRounds);

        var analyzeJob = createJob(JobType.AnalyzeIssue);
        return PlanDecision.Run(analyzeJob, round.LimitReached
            ? $"round limit {settings.Limits.MaxAnalysisRounds} reached - the job will stop it"
            : $"round {round.Number}, {settings.TriggerKeyword} comment {trigger.Id}");
    }

    /// <summary>Why ai-implement cannot run on an issue with these labels, or null when it can.</summary>
    public static string? WhyNotImplement(IReadOnlySet<string> labels) =>
        labels.Contains(Labels.Ready) && labels.Contains(Labels.Simple)
            ? null
            : $"`{Labels.Implement}` needs `{Labels.Ready}` and `{Labels.Simple}` on the issue. " +
              $"Finish the analysis first, or use `{Labels.Breakdown}` for a complex issue.";

    /// <summary>Why ai-breakdown cannot run on an issue with these labels, or null when it can.</summary>
    public static string? WhyNotBreakdown(IReadOnlySet<string> labels, string triggerKeyword) =>
        labels.Contains(Labels.Ready) && labels.Contains(Labels.Complex)
            ? null
            : $"`{Labels.Breakdown}` needs `{Labels.Ready}` and `{Labels.Complex}` on the issue. " +
              $"Finish the analysis first (answer the questions and write {triggerKeyword}).";

    /// <summary>
    /// A new @ai comment on a PR in scope asks for a review fix; it goes before a review, so the review of
    /// the fixed head covers both. Without comments the review decision is returned with NeedsComments
    /// when a fix is possible.
    /// </summary>
    public static PlanDecision PlanPull(PullPlanInput input)
    {
        var (project, repo, pull, _, botLogin, comments, lastHandledCommentId) = input;
        var settings = project.Settings;

        // A job planned while the PR was open may run after the merge: never push to or review a closed PR.
        if (!pull.IsOpen())
        {
            return PlanDecision.Skip("closed");
        }

        if (pull.Draft == true)
        {
            return PlanDecision.Skip("draft");
        }

        if (pull.Title.TrimStart().StartsWith(SkipAiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return PlanDecision.Skip(SkipAiPrefix);
        }

        // Fix scope is the scoped review rule whatever review.scope says: the fix pushes to the PR's branch.
        var fixPossible = settings.IsEnabled(Workflows.Fix) && InScope(pull, botLogin, settings.TriggerLabel);

        if (fixPossible && comments is not null)
        {
            var fixKind = new ThreadKind(Markers.ReviewFix, settings.TriggerKeyword);
            var thread = AtAiThread.Of(comments, fixKind, lastHandledCommentId);

            if (thread.Trigger is { } trigger)
            {
                var nextFix = thread.Next(settings.Limits.MaxReviewFixes);
                var triggerCommentId = trigger.Id.ToString(CultureInfo.InvariantCulture);

                return PlanDecision.Run(
                    new JobRequest(
                        JobType.FixReview,
                        repo,
                        pull.Number,
                        triggerCommentId),
                    nextFix.LimitReached
                        ? $"review fix limit {settings.Limits.MaxReviewFixes} reached - the job will say so"
                        : $"review fix {nextFix.Number}, {settings.TriggerKeyword} comment {trigger.Id}");
            }
        }

        return PlanReview(input) with
        {
            NeedsComments = fixPossible && comments is null
        };
    }

    /// <summary>
    /// Why a planned review fix must not start, or null: no build, test or lint command to check it with, the
    /// limit is reached, or the push target is not the PR's own branch at the PR head (the default branch, a
    /// fork's branch name, deleted, or moved since).
    /// </summary>
    public static string? WhyNotFix(FixCheckInput input)
    {
        var (project, target, pull, comments, defaultBranch, branchSha) = input;
        var settings = project.Settings;
        var keyword = settings.TriggerKeyword;

        if (!target.Commands.Verification().Any())
        {
            return $"No build, test or lint command is configured for component `{target.Name}` (repos.yml `commands`), " +
                   "so I cannot check a fix before pushing it. " +
                   $"Set `commands.build` or `commands.test`, then write {keyword} again.";
        }

        var fixThread = AtAiThread.Of([.. comments], new ThreadKind(Markers.ReviewFix, keyword), 0);
        if (fixThread.Next(settings.Limits.MaxReviewFixes).LimitReached)
        {
            return $"{settings.Limits.MaxReviewFixes} review fixes reached on this PR (limits.max_review_fixes). " +
                   "Please finish it by hand.";
        }

        if (pull.Head.Ref == defaultBranch)
        {
            return $"This PR comes from the default branch `{pull.Head.Ref}`; I never push to it.";
        }

        if (!string.Equals(branchSha, pull.Head.Sha, StringComparison.OrdinalIgnoreCase))
        {
            return $"Branch `{pull.Head.Ref}` in this repository is " +
                   (branchSha is null
                       ? "missing (a PR from a fork, or a deleted branch)"
                       : $"at {Text.ShortSha(branchSha)}, not at the PR head {Text.ShortSha(pull.Head.Sha)}") +
                   $". I only push to the PR's own branch here. If someone just pushed, write {keyword} again.";
        }

        return null;
    }

    private static PlanDecision PlanReview(PullPlanInput input)
    {
        var (project, repo, pull, reviewedSha, botLogin, _, _) = input;
        var settings = project.Settings;

        if (!settings.IsEnabled(Workflows.Review))
        {
            return PlanDecision.Skip("review workflow is off for this project");
        }

        if (settings.Review.IsScoped && !InScope(pull, botLogin, settings.TriggerLabel))
        {
            return PlanDecision.Skip("out of review scope (review.scope: scoped)");
        }

        var reviewRequest = new JobRequest(JobType.ReviewPullRequest, repo, pull.Number, pull.Head.Sha);

        // A person asks for one more full review with the label; it does not count toward max_reviews.
        if (IsFullReviewAsked(pull, input.FullReviewSha))
        {
            return PlanDecision.Run(reviewRequest, $"full review asked for with the label {Labels.ReviewFull}");
        }

        if (string.Equals(reviewedSha, pull.Head.Sha, StringComparison.OrdinalIgnoreCase))
        {
            return PlanDecision.Skip("head already reviewed");
        }

        // The count is local state. Without it (ai-runner.db deleted) a PR at the limit gets one more review, which
        // carries the count on from its comment tag and stores it, so the next head stops here again.
        var maxReviews = settings.Limits.MaxReviews;
        if (input.ReviewCount >= maxReviews)
        {
            return PlanDecision.Skip(
                $"review limit {maxReviews} reached (limits.max_reviews); " +
                $"the label {Labels.ReviewFull} asks for one more");
        }

        return PlanDecision.Run(reviewRequest, reviewedSha is null ? "new PR" : "new head commit");
    }

    /// <summary>
    /// The PR carries the label ai-review-full and its head has not had that full review yet (the review job removes
    /// the label, and the head it stored keeps a label that could not be removed from asking again).
    /// </summary>
    public static bool IsFullReviewAsked(HostPull pull, string? fullReviewSha) =>
        pull.LabelNames().Contains(Labels.ReviewFull) &&
        !string.Equals(fullReviewSha, pull.Head.Sha, StringComparison.OrdinalIgnoreCase);

    /// <summary>A human opts a PR in with the trigger label; PRs this runner opened itself are always in.</summary>
    public static bool InScope(HostPull pull, string botLogin, string triggerLabel) =>
        pull.LabelNames().Contains(triggerLabel) ||
        (Branches.IsAiBranch(pull.Head.Ref) &&
         string.Equals(pull.User?.Login, botLogin, StringComparison.OrdinalIgnoreCase));
}
