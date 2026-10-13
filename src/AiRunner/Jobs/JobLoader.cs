using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// An issue as a job sees it when it starts: fresh from GitBucket, and planned again. A job whose
/// type is not the decision any more (duplicate or late job) returns NotNeeded.
/// </summary>
public sealed record IssueSnapshot(
    ProjectConfig Project, GbIssue Issue, IReadOnlyList<GbComment> Comments, PlanDecision Decision);

/// <summary>A PR as a job sees it when it starts; Comments is null when the decision did not need them.</summary>
public sealed record PullSnapshot(
    ProjectConfig Project, GbPull Pull, IReadOnlyList<GbComment>? Comments, PlanDecision Decision);

/// <summary>The project and the repo (one of its code repos) a PR lives in.</summary>
public sealed record PullLocation(ProjectConfig Project, string Repo);

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
        var project = config.ProjectOf(request.Repo);
        var issue = await gitBucketClient.GetIssueAsync(request.Repo, request.Number, cancellationToken);
        var comments = await gitBucketClient.ListCommentsAsync(request.Repo, request.Number, cancellationToken);
        var lastHandledComment = stateStore.GetLastHandledComment(request.Repo, issue.Number);
        var planInput = new IssuePlanInput(project, request.Repo, issue, comments, lastHandledComment);
        var decision = JobPlanner.PlanIssue(planInput);

        return new IssueSnapshot(project, issue, comments, decision);
    }

    /// <summary>
    /// Plans a PR the way the poller and the PR jobs all do: comments are read only when the decision
    /// needs them (a review fix is possible), then the PR is planned again with them.
    /// </summary>
    public async Task<PullSnapshot> PlanPullAsync(PullLocation location, GbPull pull, CancellationToken cancellationToken)
    {
        var (project, repo) = location;
        var reviewed = stateStore.GetReview(repo, pull.Number)?.HeadSha;
        var planInput = new PullPlanInput(project, repo, pull, reviewed, identity.Login);
        var decision = JobPlanner.PlanPull(planInput);

        if (!decision.NeedsComments)
        {
            return new PullSnapshot(project, pull, null, decision);
        }

        var comments = await gitBucketClient.ListCommentsAsync(repo, pull.Number, cancellationToken);
        var lastHandledComment = stateStore.GetLastHandledComment(repo, pull.Number);
        var planInputWithComments = planInput with
        {
            Comments = comments,
            LastHandledCommentId = lastHandledComment
        };
        var decisionWithComments = JobPlanner.PlanPull(planInputWithComments);

        return new PullSnapshot(project, pull, comments, decisionWithComments);
    }

    /// <summary>Reads the PR fresh and plans it again, like LoadIssueAsync for issues.</summary>
    public async Task<PullSnapshot> LoadPullAsync(JobRequest request, CancellationToken cancellationToken)
    {
        var project = config.ProjectOf(request.Repo);
        var pull = await gitBucketClient.GetPullAsync(request.Repo, request.Number, cancellationToken);

        return await PlanPullAsync(new PullLocation(project, request.Repo), pull, cancellationToken);
    }
}
