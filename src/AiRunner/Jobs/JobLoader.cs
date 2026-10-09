using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// An issue as a job sees it when it starts: fresh from GitBucket, and planned again. A job whose
/// type is not the decision any more (duplicate or late job) returns NotNeeded.
/// </summary>
public sealed record IssueSnapshot(
    RepoConfig Repo, GbIssue Issue, IReadOnlyList<GbComment> Comments, PlanDecision Decision);

/// <summary>A PR as a job sees it when it starts; Comments is null when the decision did not need them.</summary>
public sealed record PullSnapshot(
    RepoConfig Repo, GbPull Pull, IReadOnlyList<GbComment>? Comments, PlanDecision Decision);

/// <summary>
/// Reads an issue or PR from GitBucket and plans it again with the local state. The poller and every
/// job go through here, so "should this run" has one answer. No decisions of its own: JobPlanner decides.
/// </summary>
public sealed class JobLoader(
    RunnerConfig config,
    IGitBucketClient gitBucketClient,
    IStateStore stateStore,
    RunnerIdentity identity)
{
    /// <summary>Reads the issue and its comments and asks JobPlanner again, since things changed since the poll.</summary>
    public async Task<IssueSnapshot> LoadIssueAsync(JobRequest request, CancellationToken cancellationToken)
    {
        var repo = config.RepoConfig(request.Repo);
        var issue = await gitBucketClient.GetIssueAsync(request.Repo, request.Number, cancellationToken);
        var comments = await gitBucketClient.ListCommentsAsync(request.Repo, request.Number, cancellationToken);
        var lastHandledComment = stateStore.GetLastHandledComment(request.Repo, issue.Number);
        var planInput = new IssuePlanInput(repo, issue, comments, lastHandledComment);
        var decision = JobPlanner.PlanIssue(planInput);

        return new IssueSnapshot(repo, issue, comments, decision);
    }

    /// <summary>
    /// Plans a PR the way the poller and the PR jobs all do: comments are read only when the decision
    /// needs them (a review fix is possible), then the PR is planned again with them.
    /// </summary>
    public async Task<PullSnapshot> PlanPullAsync(RepoConfig repo, GbPull pull, CancellationToken cancellationToken)
    {
        var reviewed = stateStore.GetReview(repo.FullName, pull.Number)?.HeadSha;
        var planInput = new PullPlanInput(repo, pull, reviewed, identity.Login, config.ScopeLabel);
        var decision = JobPlanner.PlanPull(planInput);

        if (!decision.NeedsComments)
        {
            return new PullSnapshot(repo, pull, null, decision);
        }

        var comments = await gitBucketClient.ListCommentsAsync(repo.FullName, pull.Number, cancellationToken);
        var lastHandledComment = stateStore.GetLastHandledComment(repo.FullName, pull.Number);
        var planInputWithComments = planInput with
        {
            Comments = comments,
            LastHandledCommentId = lastHandledComment
        };
        var decisionWithComments = JobPlanner.PlanPull(planInputWithComments);

        return new PullSnapshot(repo, pull, comments, decisionWithComments);
    }

    /// <summary>Reads the PR fresh and plans it again, like LoadIssueAsync for issues.</summary>
    public async Task<PullSnapshot> LoadPullAsync(JobRequest request, CancellationToken cancellationToken)
    {
        var repo = config.RepoConfig(request.Repo);
        var pull = await gitBucketClient.GetPullAsync(request.Repo, request.Number, cancellationToken);

        return await PlanPullAsync(repo, pull, cancellationToken);
    }
}
