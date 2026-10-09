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
    private const string SkipAiPrefix = "[skip-ai]";

    public static PlanDecision PlanIssue(IssuePlanInput input)
    {
        var (repo, issue, comments, lastHandledCommentId) = input;
        var labels = issue.LabelNames();
        var createJob = (JobType type) => new JobRequest(type, repo.FullName, issue.Number);

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
            if (!repo.IsEnabled(Workflows.Implement))
            {
                return PlanDecision.Skip("implement workflow is off for this repo");
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
            if (!repo.IsEnabled(Workflows.Breakdown))
            {
                return PlanDecision.Skip("breakdown workflow is off for this repo");
            }

            if (labels.Contains(Labels.Parent))
            {
                return PlanDecision.Skip("already broken down");
            }

            var breakdownJob = createJob(JobType.BreakdownIssue);
            var breakdownRefusal = WhyNotBreakdown(labels);
            var breakdownReason = breakdownRefusal is null
                ? $"label {Labels.Breakdown}"
                : $"label {Labels.Breakdown}, will be refused: {breakdownRefusal}";
            return PlanDecision.Run(breakdownJob, breakdownReason);
        }

        if (labels.Contains(Labels.Parent))
        {
            if (!repo.IsEnabled(Workflows.Breakdown))
            {
                return PlanDecision.Skip("breakdown workflow is off for this repo");
            }

            var sequenceJob = createJob(JobType.SequenceSubIssues);
            var sequenceDecision = PlanDecision.Run(sequenceJob, "check sub-issue progress");

            return sequenceDecision with
            {
                Routine = true
            };
        }

        if (!repo.IsEnabled(Workflows.Analyze))
        {
            return PlanDecision.Skip("analyze workflow is off for this repo");
        }

        if (comments is null)
        {
            return PlanDecision.Skip("comments not read (no change since last poll)") with
            {
                NeedsComments = true
            };
        }

        var thread = AtAiThread.Of(comments, Markers.Analysis, lastHandledCommentId);
        var trigger = thread.Trigger;

        if (thread.Done == 0)
        {
            // Sub-issues come with their scope from the breakdown; they are analyzed only on request.
            if (labels.Contains(Labels.SubIssue) && trigger is null)
            {
                return PlanDecision.Skip("sub-issue - write @ai to analyze it");
            }

            var firstRoundJob = createJob(JobType.AnalyzeIssue);
            return PlanDecision.Run(firstRoundJob, "round 1");
        }

        if (trigger is null)
        {
            return PlanDecision.Skip("wait for a new comment with @ai");
        }

        var round = thread.Next(repo.Limits.MaxAnalysisRounds);

        var analyzeJob = createJob(JobType.AnalyzeIssue);
        return PlanDecision.Run(analyzeJob, round.LimitReached
            ? $"round limit {repo.Limits.MaxAnalysisRounds} reached - the job will stop it"
            : $"round {round.Number}, @ai comment {trigger.Id}");
    }

    /// <summary>Why ai-implement cannot run on an issue with these labels, or null when it can.</summary>
    public static string? WhyNotImplement(IReadOnlySet<string> labels) =>
        labels.Contains(Labels.Ready) && labels.Contains(Labels.Simple)
            ? null
            : $"`{Labels.Implement}` needs `{Labels.Ready}` and `{Labels.Simple}` on the issue. " +
              $"Finish the analysis first, or use `{Labels.Breakdown}` for a complex issue.";

    /// <summary>Why ai-breakdown cannot run on an issue with these labels, or null when it can.</summary>
    public static string? WhyNotBreakdown(IReadOnlySet<string> labels) =>
        labels.Contains(Labels.Ready) && labels.Contains(Labels.Complex)
            ? null
            : $"`{Labels.Breakdown}` needs `{Labels.Ready}` and `{Labels.Complex}` on the issue. " +
              "Finish the analysis first (answer the questions and write @ai).";

    /// <summary>
    /// A new @ai comment on a PR in scope asks for a review fix; it goes before a review, so the review of
    /// the fixed head covers both. Without comments the review decision is returned with NeedsComments
    /// when a fix is possible.
    /// </summary>
    public static PlanDecision PlanPull(PullPlanInput input)
    {
        var (repo, pull, _, botLogin, scopeLabel, comments, lastHandledCommentId) = input;

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
        var fixPossible = repo.IsEnabled(Workflows.Fix) && InScope(pull, botLogin, scopeLabel);

        if (fixPossible && comments is not null)
        {
            var thread = AtAiThread.Of(comments, Markers.ReviewFix, lastHandledCommentId);

            if (thread.Trigger is { } trigger)
            {
                var nextFix = thread.Next(repo.Limits.MaxReviewFixes);
                var triggerCommentId = trigger.Id.ToString(CultureInfo.InvariantCulture);

                return PlanDecision.Run(
                    new JobRequest(
                        JobType.FixReview,
                        repo.FullName,
                        pull.Number,
                        triggerCommentId),
                    nextFix.LimitReached
                        ? $"review fix limit {repo.Limits.MaxReviewFixes} reached - the job will say so"
                        : $"review fix {nextFix.Number}, @ai comment {trigger.Id}");
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
        var (repo, pull, comments, defaultBranch, branchSha) = input;

        if (!repo.Commands.Verification().Any())
        {
            return "No build, test or lint command is configured for this repo (repos.yml `commands`), so I cannot check a fix " +
                   "before pushing it. Set `commands.build` or `commands.test`, then write @ai again.";
        }

        if (AtAiThread.Of([.. comments], Markers.ReviewFix, 0).Next(repo.Limits.MaxReviewFixes).LimitReached)
        {
            return $"{repo.Limits.MaxReviewFixes} review fixes reached on this PR (limits.maxReviewFixes). Please finish it by hand.";
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
                   ". I only push to the PR's own branch here. If someone just pushed, write @ai again.";
        }

        return null;
    }

    private static PlanDecision PlanReview(PullPlanInput input)
    {
        var (repo, pull, reviewedSha, botLogin, scopeLabel, _, _) = input;

        if (!repo.IsEnabled(Workflows.Review))
        {
            return PlanDecision.Skip("review workflow is off for this repo");
        }

        if (repo.Review.IsScoped && !InScope(pull, botLogin, scopeLabel))
        {
            return PlanDecision.Skip("out of review scope (review.scope: scoped)");
        }

        if (string.Equals(reviewedSha, pull.Head.Sha, StringComparison.OrdinalIgnoreCase))
        {
            return PlanDecision.Skip("head already reviewed");
        }

        return PlanDecision.Run(
            new JobRequest(JobType.ReviewPullRequest, repo.FullName, pull.Number, pull.Head.Sha),
            reviewedSha is null ? "new PR" : "new head commit");
    }

    /// <summary>A human opts a PR in with the scope label; PRs this runner opened itself are always in.</summary>
    private static bool InScope(GbPull pull, string botLogin, string scopeLabel) =>
        pull.LabelNames().Contains(scopeLabel) ||
        (Branches.IsAiBranch(pull.Head.Ref) &&
         string.Equals(pull.User?.Login, botLogin, StringComparison.OrdinalIgnoreCase));
}
