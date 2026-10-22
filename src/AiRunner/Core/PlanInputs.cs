using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>What <see cref="JobPlanner.PlanIssue"/> needs to decide which job an issue needs.</summary>
/// <param name="Repo">The repo that holds the issue: the project's issues repo.</param>
/// <param name="Comments">Null when the comments were not read; the decision then says NeedsComments if it needs them.</param>
/// <param name="LastHandledCommentId">Newest trigger comment an analysis already answered (local state, 0 = none).</param>
public sealed record IssuePlanInput(
    ProjectConfig Project,
    string Repo,
    HostIssue Issue,
    IReadOnlyList<HostComment>? Comments,
    long LastHandledCommentId);

/// <summary>What <see cref="JobPlanner.PlanPull"/> needs to decide between a review fix, a review or nothing.</summary>
/// <param name="Repo">The repo of the PR: the repo of one of the project's code components.</param>
/// <param name="ReviewedSha">Head commit already reviewed (local state), or null.</param>
/// <param name="Comments">The PR's comments, or null when they were not read.</param>
/// <param name="LastHandledCommentId">Newest trigger comment a review fix already answered (local state, 0 = none).</param>
public sealed record PullPlanInput(
    ProjectConfig Project,
    string Repo,
    HostPull Pull,
    string? ReviewedSha,
    string BotLogin,
    IReadOnlyList<HostComment>? Comments = null,
    long LastHandledCommentId = 0)
{
    /// <summary>Automatic reviews of this PR so far (local state; the review job checks the comment tag too).</summary>
    public int ReviewCount { get; init; }

    /// <summary>Head commit that already got the full review the label ai-review-full asked for, or null.</summary>
    public string? FullReviewSha { get; init; }
}

/// <summary>What <see cref="JobPlanner.WhyNotFix"/> checks before a review fix may start.</summary>
/// <param name="Target">The component the fix changes; its commands check the fix.</param>
/// <param name="BranchSha">What origin/{head ref} points to in the clone, or null when it does not exist.</param>
public sealed record FixCheckInput(
    ProjectConfig Project,
    ComponentConfig Target,
    HostPull Pull,
    IEnumerable<HostComment> Comments,
    string DefaultBranch,
    string? BranchSha);
