using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>What the final PR of a finished breakdown is built from.</summary>
/// <param name="SubIssueNumbers">The sub-issue numbers in build order.</param>
/// <param name="Pulls">Open and closed PRs of the repo.</param>
public sealed record FinalPullInput(
    string Repo,
    GbIssue Parent,
    IReadOnlyList<int> SubIssueNumbers,
    IReadOnlyList<GbPull> Pulls);

/// <summary>
/// Workflow 1b sequencing, on every poll for each ai:parent issue: when a sub-issue PR is
/// merged into feat/issue-{n}, start the next sub-issue; after the last one, open the final
/// PR to the default branch. API calls only, no Copilot run. The decision is SubIssueSequencer's.
/// </summary>
public sealed class SequenceSubIssuesJob(
    IGitBucketClient gitBucketClient,
    JobLoader jobLoader) : IJob
{
    public JobType Type => JobType.SequenceSubIssues;

    public JobInfo Info
    {
        get
        {
            var timeout = TimeSpan.FromMinutes(10);
            return new(Workflows.Breakdown, "Sub-issue sequencing", timeout);
        }
    }

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken cancellationToken)
    {
        var (_, parent, comments, decision) = await jobLoader.LoadIssueAsync(request, cancellationToken);
        if (decision.Job?.Type != Type)
        {
            return JobOutcome.NotNeeded(decision.Reason);
        }

        var list = SubIssueBody.FindList(comments)
            ?? throw new JobFailedException("This parent issue has no sub-issue list in its comments.");
        var subIssues = new List<GbIssue>();
        foreach (var number in list)
        {
            var subIssue = await gitBucketClient.GetIssueAsync(request.Repo, number, cancellationToken);
            subIssues.Add(subIssue);
        }

        List<GbPull> pulls =
        [
            .. await gitBucketClient.ListPullsAsync(request.Repo, "open", cancellationToken),
            .. await gitBucketClient.ListPullsAsync(request.Repo, "closed", cancellationToken),
        ];

        var step = SubIssueSequencer.Decide(parent.Number, subIssues, pulls);
        switch (step.Step)
        {
            case SequenceStep.StartNext:
                var labelChange = LabelChange.Adding(Labels.Implement);
                await gitBucketClient.ApplyLabelsAsync(request.Repo, step.Next!.Number, labelChange, cancellationToken);
                await gitBucketClient.AddCommentAsync(
                    request.Repo,
                    step.Next.Number,
                    $"#{step.Previous!.Number} is done (merged into `{Branches.Feature(parent.Number)}`). " +
                    "Starting this sub-issue.",
                    cancellationToken);
                run.Info($"started sub-issue #{step.Next.Number}");
                return JobOutcome.Ok($"started #{step.Next.Number}");

            case SequenceStep.OpenFinalPr:
                var finalPullInput = new FinalPullInput(request.Repo, parent, list, pulls);
                var final = await OpenFinalPullAsync(finalPullInput, cancellationToken);
                return JobOutcome.Ok($"final PR #{final.Number}");

            default:
                return JobOutcome.NotNeeded(step.Reason);
        }
    }

    private async Task<GbPull> OpenFinalPullAsync(FinalPullInput input, CancellationToken cancellationToken)
    {
        var (repo, parent, list, pulls) = input;
        var feature = Branches.Feature(parent.Number);
        var defaultBranch = await gitBucketClient.DefaultBranchAsync(repo, cancellationToken);

        var body = new StringBuilder();
        body.AppendLine($"All sub-issues of #{parent.Number} are done. This PR brings `{feature}` into `{defaultBranch}`.");
        body.AppendLine();
        body.AppendLine("Sub-issue PRs:");
        for (var order = 1; order <= list.Count; order++)
        {
            var subIssuePull = pulls.FirstOrDefault(
                pull => pull.Head.Ref == Branches.SubIssue(parent.Number, order) && pull.IsMerged());
            body.AppendLine(subIssuePull is null
                ? $"- #{list[order - 1]}: closed without a runner PR"
                : $"- #{subIssuePull.Number}: {subIssuePull.Title} (sub-issue #{list[order - 1]})");
        }

        body.AppendLine();
        body.AppendLine($"Closes #{parent.Number}");

        var title = $"#{parent.Number}: {parent.Title}";
        var bodyText = body.ToString();
        var newPull = new NewPull(title, feature, defaultBranch, bodyText);
        var final = await gitBucketClient.CreatePullAsync(repo, newPull, cancellationToken);
        await gitBucketClient.AddCommentAsync(
            repo,
            parent.Number,
            $"All sub-issues are merged. Final PR #{final.Number} is open: `{feature}` -> `{defaultBranch}`.",
            cancellationToken);

        return final;
    }
}
