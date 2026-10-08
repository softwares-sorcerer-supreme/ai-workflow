using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// Workflow 1b sequencing, on every poll for each ai:parent issue: when a sub-issue PR is
/// merged into feat/issue-{n}, start the next sub-issue; after the last one, open the final
/// PR to the default branch. API calls only, no Copilot run. The decision is SubIssueSequencer's.
/// </summary>
public sealed class SequenceSubIssuesJob(
    RunnerConfig config,
    IGitBucketClient gitBucket,
    IStateStore state) : IJob
{
    public JobType Type => JobType.SequenceSubIssues;

    public JobInfo Info => new(Workflows.Breakdown, "Sub-issue sequencing", TimeSpan.FromMinutes(10));

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken ct)
    {
        var (_, parent, comments, decision) = await gitBucket.LoadIssueAsync(config, state, request, ct);
        if (decision.Job?.Type != Type)
            return JobOutcome.NotNeeded(decision.Reason);

        var list = SubIssueBody.FindList(comments)
            ?? throw new JobFailedException("This parent issue has no sub-issue list in its comments.");
        var subs = new List<GbIssue>();
        foreach (var number in list)
            subs.Add(await gitBucket.GetIssueAsync(request.Repo, number, ct));
        List<GbPull> pulls = [.. await gitBucket.ListPullsAsync(request.Repo, "open", ct),
                              .. await gitBucket.ListPullsAsync(request.Repo, "closed", ct)];

        var step = SubIssueSequencer.Decide(parent.Number, subs, pulls);
        switch (step.Step)
        {
            case SequenceStep.StartNext:
                await gitBucket.ApplyLabelsAsync(request.Repo, step.Next!.Number, [Labels.Implement], [], ct);
                await gitBucket.AddCommentAsync(request.Repo, step.Next.Number,
                    $"#{step.Previous!.Number} is done (merged into `{Branches.Feature(parent.Number)}`). Starting this sub-issue.", ct);
                run.Info($"started sub-issue #{step.Next.Number}");
                return JobOutcome.Ok($"started #{step.Next.Number}");

            case SequenceStep.OpenFinalPr:
                var final = await OpenFinalPullAsync(request.Repo, parent, list, pulls, ct);
                return JobOutcome.Ok($"final PR #{final.Number}");

            default:
                return JobOutcome.NotNeeded(step.Reason);
        }
    }

    private async Task<GbPull> OpenFinalPullAsync(string repo, GbIssue parent, IReadOnlyList<int> list,
        IReadOnlyList<GbPull> pulls, CancellationToken ct)
    {
        var feature = Branches.Feature(parent.Number);
        var defaultBranch = await gitBucket.DefaultBranchAsync(repo, ct);
        var body = new StringBuilder();
        body.AppendLine($"All sub-issues of #{parent.Number} are done. This PR brings `{feature}` into `{defaultBranch}`.");
        body.AppendLine();
        body.AppendLine("Sub-issue PRs:");
        for (var order = 1; order <= list.Count; order++)
        {
            var pr = pulls.FirstOrDefault(p => p.Head.Ref == Branches.SubIssue(parent.Number, order) && p.IsMerged());
            body.AppendLine(pr is null
                ? $"- #{list[order - 1]}: closed without a runner PR"
                : $"- #{pr.Number}: {pr.Title} (sub-issue #{list[order - 1]})");
        }
        body.AppendLine();
        body.AppendLine($"Closes #{parent.Number}");

        var final = await gitBucket.CreatePullAsync(repo, $"#{parent.Number}: {parent.Title}", feature, defaultBranch, body.ToString(), ct);
        await gitBucket.AddCommentAsync(repo, parent.Number,
            $"All sub-issues are merged. Final PR #{final.Number} is open: `{feature}` -> `{defaultBranch}`.", ct);
        return final;
    }
}
