using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Jobs;

/// <summary>
/// Closes a simple issue after its PR is merged. The PR is in the repo of the issue's component, which is not the
/// issues repo in most layouts, so GitBucket cannot close the issue by itself (the PR has no "Closes" line). The
/// state is read from GitBucket on every poll, so a runner that was off during the merge catches up. API calls
/// only, no Copilot run.
/// </summary>
public sealed class FinishIssueJob(
    ICodeHost codeHost,
    JobLoader jobLoader) : IJob
{
    public JobType Type => JobType.FinishIssue;

    public JobInfo Info
    {
        get
        {
            var timeout = TimeSpan.FromMinutes(10);
            return new(Workflows.Implement, "Issue finish", timeout, Routine: true);
        }
    }

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken cancellationToken)
    {
        var (project, issue, comments, decision) = await jobLoader.LoadIssueAsync(request, cancellationToken);
        if (decision.Job?.Type != Type)
        {
            return JobOutcome.NotNeeded(decision.Reason);
        }

        // A routine check must not fail every poll: an issue that names no target was not built by the runner.
        ComponentConfig target;
        try
        {
            target = project.TargetOfIssue(comments);
        }
        catch (JobFailedException exception)
        {
            return JobOutcome.NotNeeded(exception.Message);
        }

        var allPulls = await codeHost.ListAllPullsAsync(target.Repo, cancellationToken);
        var pulls = IssuePulls.OfIssue(issue.Number, allPulls);
        var outcome = IssuePulls.OutcomeOf(pulls);

        // A PR is still open, or none was made yet: nothing to do. The implement job owns that state.
        if (outcome is PullOutcome.None or PullOutcome.Open)
        {
            return JobOutcome.NotNeeded("no merged PR yet");
        }

        var merged = pulls.FirstOrDefault(pull => pull.IsMerged());

        // Both ways below write to the issue, so this is the moment to claim it.
        if (!await run.ClaimAsync(cancellationToken))
        {
            return JobOutcome.NotNeeded("claimed by another runner");
        }

        if (merged is not null)
        {
            var mergedRef = JobSupport.PullRef(merged.Number, request.Repo, target.Repo);
            var comment = $"PR {mergedRef} is merged into `{merged.Base.Ref}`. This issue is done, so I close it.";
            run.Step(JobSteps.Close, "Close issue");
            await codeHost.CloseDoneAsync(request.Repo, issue.Number, comment, cancellationToken);
            run.Info($"closed issue #{issue.Number} (PR {mergedRef} merged)");

            return JobOutcome.Ok($"closed after {mergedRef}", copilotCalls: 0);
        }

        run.Step(JobSteps.Comment, "Comment");
        var closedRef = JobSupport.PullRef(pulls[0].Number, request.Repo, target.Repo);
        await codeHost.AddCommentAsync(
            request.Repo,
            issue.Number,
            $"PR {closedRef} was closed without a merge. I stop watching this issue. " +
            $"Add `{Labels.Implement}` to try again.",
            cancellationToken);
        await codeHost.RemoveLabelAsync(request.Repo, issue.Number, Labels.Implementing, cancellationToken);

        return JobOutcome.Ok($"{closedRef} closed without a merge", copilotCalls: 0);
    }
}
