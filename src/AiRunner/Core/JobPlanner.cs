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

    /// <param name="comments">Null when the comments were not read; the decision then says NeedsComments if it needs them.</param>
    /// <param name="lastHandledCommentId">Newest @ai comment an analysis already answered (local state, 0 = none).</param>
    public static PlanDecision PlanIssue(RepoConfig repo, GbIssue issue, IReadOnlyList<GbComment>? comments, long lastHandledCommentId)
    {
        var labels = issue.LabelNames();
        var job = (JobType type) => new JobRequest(type, repo.FullName, issue.Number);

        if (!issue.IsOpen())
            return PlanDecision.Skip("closed");
        if (labels.Contains(Labels.Failed))
            return PlanDecision.Skip($"{Labels.Failed} - a human must read the log and remove the label to retry");
        if (labels.Contains(Labels.NeedsCloudAgent) || labels.Contains(Labels.OnGitHub))
            return PlanDecision.Skip("cloud fallback (Workflow 4) is not built yet");

        if (labels.Contains(Labels.Implement))
        {
            if (!repo.IsEnabled(Workflows.Implement)) return PlanDecision.Skip("implement workflow is off for this repo");
            if (labels.Contains(Labels.Implementing)) return PlanDecision.Skip("already implementing");
            // A refused request still becomes a job: the job explains why and removes the label.
            return PlanDecision.Run(job(JobType.ImplementIssue), WhyNotImplement(labels) is { } why
                ? $"label {Labels.Implement}, will be refused: {why}"
                : $"label {Labels.Implement}");
        }

        if (labels.Contains(Labels.Breakdown))
        {
            if (!repo.IsEnabled(Workflows.Breakdown)) return PlanDecision.Skip("breakdown workflow is off for this repo");
            if (labels.Contains(Labels.Parent)) return PlanDecision.Skip("already broken down");
            return PlanDecision.Run(job(JobType.BreakdownIssue), WhyNotBreakdown(labels) is { } why
                ? $"label {Labels.Breakdown}, will be refused: {why}"
                : $"label {Labels.Breakdown}");
        }

        if (labels.Contains(Labels.Parent))
        {
            return repo.IsEnabled(Workflows.Breakdown)
                ? PlanDecision.Run(job(JobType.SequenceSubIssues), "check sub-issue progress") with { Routine = true }
                : PlanDecision.Skip("breakdown workflow is off for this repo");
        }

        if (!repo.IsEnabled(Workflows.Analyze))
            return PlanDecision.Skip("analyze workflow is off for this repo");
        if (comments is null)
            return PlanDecision.Skip("comments not read (no change since last poll)") with { NeedsComments = true };

        var analyses = comments.Where(BotComments.IsAnalysis).OrderBy(c => c.Id).ToList();
        var last = analyses.LastOrDefault();
        var afterId = Math.Max(lastHandledCommentId, last is null ? 0 : BotComments.ContextUpto(last) ?? last.Id);
        var trigger = comments.Where(c => c.Id > afterId && BotComments.MentionsAi(c)).MaxBy(c => c.Id);

        if (analyses.Count == 0)
        {
            // Sub-issues come with their scope from the breakdown; they are analyzed only on request.
            if (labels.Contains(Labels.SubIssue) && trigger is null)
                return PlanDecision.Skip("sub-issue - write @ai to analyze it");
            return PlanDecision.Run(job(JobType.AnalyzeIssue), "round 1");
        }

        if (trigger is null)
            return PlanDecision.Skip("wait for a new comment with @ai");
        var round = NextRound(repo, comments);
        return PlanDecision.Run(job(JobType.AnalyzeIssue), round.LimitReached
            ? $"round limit {repo.Limits.MaxAnalysisRounds} reached - the job will stop it"
            : $"round {round.Number}, @ai comment {trigger.Id}");
    }

    /// <summary>The number of the next analysis round, and whether it is over the limit.</summary>
    public static (int Number, bool LimitReached) NextRound(RepoConfig repo, IEnumerable<GbComment> comments)
    {
        var done = comments.Count(BotComments.IsAnalysis);
        return (done + 1, done >= repo.Limits.MaxAnalysisRounds);
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

    /// <param name="reviewedSha">Head commit already reviewed (local state), or null.</param>
    public static PlanDecision PlanPull(RepoConfig repo, GbPull pull, string? reviewedSha, string botLogin, string scopeLabel)
    {
        if (!repo.IsEnabled(Workflows.Review))
            return PlanDecision.Skip("review workflow is off for this repo");
        if (pull.Draft == true)
            return PlanDecision.Skip("draft");
        if (pull.Title.TrimStart().StartsWith(SkipAiPrefix, StringComparison.OrdinalIgnoreCase))
            return PlanDecision.Skip(SkipAiPrefix);
        if (repo.Review.IsScoped && !InScope(pull, botLogin, scopeLabel))
            return PlanDecision.Skip("out of review scope (review.scope: scoped)");
        if (string.Equals(reviewedSha, pull.Head.Sha, StringComparison.OrdinalIgnoreCase))
            return PlanDecision.Skip("head already reviewed");

        return PlanDecision.Run(new JobRequest(JobType.ReviewPullRequest, repo.FullName, pull.Number, pull.Head.Sha),
            reviewedSha is null ? "new PR" : "new head commit");
    }

    /// <summary>A human opts a PR in with the scope label; PRs this runner opened itself are always in.</summary>
    private static bool InScope(GbPull pull, string botLogin, string scopeLabel) =>
        pull.LabelNames().Contains(scopeLabel) ||
        (Branches.IsAiBranch(pull.Head.Ref) && string.Equals(pull.User?.Login, botLogin, StringComparison.OrdinalIgnoreCase));
}
