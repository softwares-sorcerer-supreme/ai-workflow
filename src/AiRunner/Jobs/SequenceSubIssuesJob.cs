using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>What one final PR (one repo, from the feature branch into the default branch) is built from.</summary>
/// <param name="Repo">The repo the final PR is opened in.</param>
/// <param name="SubIssues">All sub-issues of the parent, in build order, with their repos.</param>
/// <param name="Pulls">Open and closed PRs of that repo.</param>
public sealed record FinalPullInput(
    string Repo,
    GbIssue Parent,
    string IssuesRepo,
    IReadOnlyList<SubIssueRef> SubIssues,
    IReadOnlyList<GbPull> Pulls);

/// <summary>The final PRs of a parent, one per repo, for the comment on the parent.</summary>
public sealed record FinalPullList(
    IReadOnlyList<(string Repo, GbPull Pull)> Pulls,
    string FeatureBranch);

/// <summary>
/// Workflow 1b sequencing, on every poll for each ai:parent issue: when a sub-issue PR is merged into
/// feat/issue-{n} in the repo of its component, the job comments the PR on the sub-issue and closes it, then
/// starts the next sub-issue. After the last one it opens one final PR per repo that has sub-issues, and when all
/// of them are merged it closes the parent. API calls only, no Copilot run. The decision is SubIssueSequencer's.
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
            return new(Workflows.Breakdown, "Sub-issue sequencing", timeout, Routine: true);
        }
    }

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken cancellationToken)
    {
        var (project, parent, comments, decision) = await jobLoader.LoadIssueAsync(request, cancellationToken);
        if (decision.Job?.Type != Type)
        {
            return JobOutcome.NotNeeded(decision.Reason);
        }

        var list = SubIssueBody.FindList(comments)
            ?? throw new JobFailedException("This parent issue has no sub-issue list in its comments.");
        var subIssues = new List<SubIssueRef>();
        foreach (var number in list)
        {
            var subIssue = await gitBucketClient.GetIssueAsync(request.Repo, number, cancellationToken);

            // The PRs of a sub-issue are in the repo of its component. Without a component on the sub-issue
            // (written before components existed) only a single code component leaves no doubt which repo that is.
            var info = JobSupport.Parse(() => SubIssueBody.Parse(subIssue.Body), $"Sub-issue #{number}");
            var component = info?.Component is { } name ? project.TargetComponent(name) : project.SoleCodeComponent;
            if (component is null)
            {
                return JobOutcome.NotNeeded("several code components and no component on the sub-issues yet");
            }

            subIssues.Add(new SubIssueRef(subIssue, component.Repo));
        }

        var pullsByRepo = new Dictionary<string, IReadOnlyList<GbPull>>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in SubIssueRef.ReposOf(subIssues))
        {
            pullsByRepo[repo] = await gitBucketClient.ListAllPullsAsync(repo, cancellationToken);
        }

        var input = new SequenceInput(parent.Number, subIssues, pullsByRepo)
        {
            HasFinalList = SubIssueBody.HasFinalList(comments),
        };

        var merged = SubIssueSequencer.MergedButOpen(input);
        var step = SubIssueSequencer.Decide(input);

        // Nothing to write on most polls. With claims on, the first write is the moment to claim the parent.
        var abandoned = SubIssueSequencer.ClosedWithoutMerge(input);
        var hasWork = merged.Count > 0 || abandoned.Count > 0 || step.Step != SequenceStep.Wait;
        if (hasWork && !await run.ClaimAsync(cancellationToken))
        {
            return JobOutcome.NotNeeded("claimed by another runner");
        }

        // A merged sub-issue PR closes its sub-issue here, because GitBucket does not close an issue of another repo.
        foreach (var mergedSubIssue in merged)
        {
            await CloseMergedSubIssueAsync(request.Repo, parent.Number, mergedSubIssue, cancellationToken);
            var closedNumber = mergedSubIssue.SubIssue.Issue.Number;
            run.Info($"closed sub-issue #{closedNumber} (PR #{mergedSubIssue.Pull.Number} merged)");
        }

        // A PR closed on purpose: stop watching that sub-issue until a human removes ai:failed.
        foreach (var abandonedSubIssue in abandoned)
        {
            await StopAbandonedSubIssueAsync(request.Repo, abandonedSubIssue, cancellationToken);
            var abandonedNumber = abandonedSubIssue.SubIssue.Issue.Number;
            run.Info($"sub-issue #{abandonedNumber}: PR #{abandonedSubIssue.Pull.Number} closed without a merge");
        }

        var closedSummary = $"closed {merged.Count} merged sub-issue(s)";
        var closedNote = merged.Count == 0 ? "" : closedSummary + "; ";
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
                return JobOutcome.Ok($"{closedNote}started #{step.Next.Number}");

            case SequenceStep.OpenFinalPrs:
                var opened = await OpenFinalPrsAsync(project, input, parent, cancellationToken);
                return JobOutcome.Ok($"{closedNote}final PRs: {opened}");

            case SequenceStep.CloseParent:
                await gitBucketClient.AddCommentAsync(
                    request.Repo,
                    parent.Number,
                    "All final PRs are merged. This issue is done, so I close it.",
                    cancellationToken);
                await gitBucketClient.CloseIssueAsync(request.Repo, parent.Number, cancellationToken);
                return JobOutcome.Ok($"{closedNote}parent #{parent.Number} closed");

            default:
                if (merged.Count > 0)
                {
                    return JobOutcome.Ok(closedSummary);
                }

                return abandoned.Count > 0
                    ? JobOutcome.Ok($"{abandoned.Count} sub-issue(s) stopped, PR closed without a merge")
                    : JobOutcome.NotNeeded(step.Reason);
        }
    }

    /// <summary>Comments the merged PR on the sub-issue and closes it; ai:implementing is done with.</summary>
    private async Task CloseMergedSubIssueAsync(
        string issuesRepo,
        int parent,
        MergedSubIssue merged,
        CancellationToken cancellationToken)
    {
        var (subIssue, pull) = merged;
        var number = subIssue.Issue.Number;
        var pullRef = JobSupport.PullRef(pull.Number, issuesRepo, subIssue.Repo);

        var comment = $"PR {pullRef} is merged into `{Branches.Feature(parent)}`. This sub-issue is done, so I close it.";
        await gitBucketClient.CloseDoneAsync(issuesRepo, number, comment, cancellationToken);
    }

    /// <summary>
    /// Comments that the PR was closed without a merge, drops ai:implementing and adds ai:failed, so the sequencer
    /// waits for a human instead of starting the sub-issue again at once.
    /// </summary>
    private async Task StopAbandonedSubIssueAsync(
        string issuesRepo,
        AbandonedSubIssue abandoned,
        CancellationToken cancellationToken)
    {
        var (subIssue, pull) = abandoned;
        var number = subIssue.Issue.Number;
        var pullRef = JobSupport.PullRef(pull.Number, issuesRepo, subIssue.Repo);

        await gitBucketClient.AddCommentAsync(
            issuesRepo,
            number,
            $"PR {pullRef} was closed without a merge, so I stop here. To build this sub-issue again, " +
            $"remove the `{Labels.Failed}` label.",
            cancellationToken);

        var change = new LabelChange(Add: [Labels.Failed], Remove: [Labels.Implementing]);
        await gitBucketClient.ApplyLabelsAsync(issuesRepo, number, change, cancellationToken);
    }

    /// <summary>
    /// Opens the missing final PRs, one per repo, then the one comment on the parent that lists all of them.
    /// Returns the numbers of the PRs opened now, for the log.
    /// </summary>
    private async Task<string> OpenFinalPrsAsync(
        ProjectConfig project,
        SequenceInput input,
        GbIssue parent,
        CancellationToken cancellationToken)
    {
        var issuesRepo = project.IssuesRepo;
        var feature = Branches.Feature(parent.Number);
        var openedNow = new List<string>();
        var finalPulls = new List<(string Repo, GbPull Pull)>();

        foreach (var repo in input.Repos)
        {
            var existing = SubIssueSequencer.FinalPull(input.PullsOf(repo), feature);
            if (existing is null)
            {
                var repoPulls = input.PullsOf(repo);
                var finalPullInput = new FinalPullInput(repo, parent, issuesRepo, input.SubIssues, repoPulls);
                existing = await OpenFinalPullAsync(finalPullInput, cancellationToken);
                openedNow.Add($"{repo}#{existing.Number}");
            }

            finalPulls.Add((repo, existing));
        }

        // A new list is posted when a final PR was replaced, so the parent never points at a closed one.
        if (!input.HasFinalList || openedNow.Count > 0)
        {
            var list = new FinalPullList(finalPulls, feature);
            var defaultBranches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (repo, _) in finalPulls)
            {
                defaultBranches[repo] = await gitBucketClient.DefaultBranchAsync(repo, cancellationToken);
            }

            var comment = FinalListComment(list, issuesRepo, defaultBranches);
            await gitBucketClient.AddCommentAsync(issuesRepo, parent.Number, comment, cancellationToken);
        }

        return openedNow.Count == 0 ? "listed" : string.Join(", ", openedNow);
    }

    private async Task<GbPull> OpenFinalPullAsync(FinalPullInput input, CancellationToken cancellationToken)
    {
        var (repo, parent, issuesRepo, subIssues, pulls) = input;
        var feature = Branches.Feature(parent.Number);
        var defaultBranch = await gitBucketClient.DefaultBranchAsync(repo, cancellationToken);

        var body = new StringBuilder();
        body.AppendLine($"All sub-issues of #{parent.Number} are done.");
        body.AppendLine($"This PR brings `{feature}` into `{defaultBranch}`.");
        body.AppendLine();
        body.AppendLine("Sub-issue PRs in this repo:");
        for (var order = 1; order <= subIssues.Count; order++)
        {
            var (subIssue, subIssueRepo) = subIssues[order - 1];
            if (!string.Equals(subIssueRepo, repo, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slot = new SubIssueSlot(parent.Number, order);
            var subIssuePull = SubIssueSequencer.MergedPull(slot, pulls);
            body.AppendLine(subIssuePull is null
                ? $"- {issuesRepo}#{subIssue.Number}: closed without a runner PR"
                : $"- #{subIssuePull.Number}: {subIssuePull.Title} (sub-issue {issuesRepo}#{subIssue.Number})");
        }

        // Never "Closes": the issue is in another repo in most layouts, and the runner closes it itself.
        body.AppendLine();
        body.AppendLine($"Issue: {issuesRepo}#{parent.Number}");

        var title = $"#{parent.Number}: {parent.Title}";
        var bodyText = body.ToString();
        var newPull = new NewPull(title, feature, defaultBranch, bodyText);

        return await gitBucketClient.CreatePullAsync(repo, newPull, cancellationToken);
    }

    private static string FinalListComment(
        FinalPullList list,
        string issuesRepo,
        IReadOnlyDictionary<string, string> defaultBranches)
    {
        var builder = new StringBuilder();
        builder.AppendLine("All sub-issues are merged. Final PRs, one per repo:");
        builder.AppendLine();
        foreach (var (repo, pull) in list.Pulls)
        {
            var pullRef = JobSupport.PullRef(pull.Number, issuesRepo, repo);
            builder.AppendLine($"- {pullRef}: `{list.FeatureBranch}` -> `{defaultBranches[repo]}`");
        }

        builder.AppendLine();
        builder.AppendLine("When all of them are merged, I close this issue.");
        builder.AppendLine(SubIssueBody.FinalPrsMarker);

        return builder.ToString();
    }
}
