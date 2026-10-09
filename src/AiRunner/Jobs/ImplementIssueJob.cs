using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>What ImplementIssueJob works on once it has an issue, its branches and a clone.</summary>
/// <param name="BaseBranch">The branch the PR goes into.</param>
/// <param name="Branch">The branch the work is pushed to.</param>
/// <param name="Plan">The text of plan.md.</param>
public sealed record ImplementationRequest(
    RepoConfig Repo,
    GbIssue Issue,
    string Directory,
    string BaseBranch,
    string Branch,
    string Plan)
{
    /// <summary>Files for the Copilot --attachment flag (round 1 only).</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];
}

/// <summary>
/// Workflow 2: a human added ai-implement on a simple issue or sub-issue. Copilot writes the
/// code on a branch and goes through the stages itself (verify, review and simplify, PR_BODY.md).
/// The runner then checks build, test, lint and PR_BODY.md on its own, sends failures back for up to
/// implement.maxFixRounds fix rounds, enforces the size limits, commits, pushes and opens the PR.
/// The agent never runs git and never merges. Whatever the agent wrote is
/// pushed to the branch even when the run fails or the change is too big (no PR then), so no
/// work is lost; a retry continues from that branch.
/// </summary>
public sealed class ImplementIssueJob(
    RunnerConfig config,
    IGitBucketClient gitBucketClient,
    JobLoader jobLoader,
    IWorkspace workspace,
    IGitClient git,
    AgentSession session,
    AttachmentFetcher attachmentFetcher) : IJob
{
    public const string Agent = "implement-issue";
    // The stages and the follow-up procedure live only in agents/implement-issue.agent.md.
    public const string Prompt =
        "Implement the plan in plan.md. Follow the implement-issue agent rules and AGENTS.md; finish every stage before you stop.";
    public const string FixPrompt =
        "Follow-up round: read fix.md and follow the Follow-up round section of the implement-issue agent rules.";

    public JobType Type => JobType.ImplementIssue;

    public JobInfo Info
    {
        get
        {
            var timeout = AgentSession.JobTimeout(config);
            return new(Workflows.Implement, "Implementation", timeout);
        }
    }

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken cancellationToken)
    {
        var (repo, issue, comments, decision) = await jobLoader.LoadIssueAsync(request, cancellationToken);
        if (decision.Job?.Type != Type)
        {
            return JobOutcome.NotNeeded(decision.Reason);
        }

        var labelNames = issue.LabelNames();
        var rejectionReason = JobPlanner.WhyNotImplement(labelNames);
        if (rejectionReason is not null)
        {
            return await RejectAsync(request.Repo, issue, rejectionReason, cancellationToken);
        }

        // Base branch, work branch and plan: a sub-issue builds on the parent's feature branch.
        var subIssue = issue.HasLabel(Labels.SubIssue)
            ? JobSupport.Parse(() => SubIssueBody.Parse(issue.Body), "The sub-issue body")
            : null;
        string baseBranch, branch, plan;
        if (subIssue is not null)
        {
            var waitingFor = await PreviousSubIssueNotDoneAsync(request.Repo, subIssue, cancellationToken);
            if (waitingFor is not null)
            {
                var waitingReason =
                    $"Sub-issues are built in order. #{waitingFor} (#{subIssue.Parent}.{subIssue.Order - 1}) " +
                    $"is not merged yet; I add `{Labels.Implement}` here automatically when it is.";

                return await RejectAsync(request.Repo, issue, waitingReason, cancellationToken);
            }

            baseBranch = subIssue.BaseBranch;
            branch = Branches.SubIssue(subIssue.Parent, subIssue.Order);
            plan = ContextBuilder.PlanFromSubIssue(issue);
        }
        else
        {
            var spec = JobSupport.LatestBlock(comments, Markers.Spec)
                ?? throw new JobFailedException("No SPEC comment on this issue. Run the analysis until Status is ready.");
            baseBranch = await gitBucketClient.DefaultBranchAsync(request.Repo, cancellationToken);
            branch = Branches.Issue(issue.Number);
            plan = ContextBuilder.PlanFromSpec(issue, spec);
        }

        if (branch == baseBranch)
        {
            throw new JobFailedException($"Refusing to work on the base branch {baseBranch}.");
        }

        // A PR from this branch is already open (e.g. an earlier run stopped right after opening it).
        var openPulls = await gitBucketClient.ListPullsAsync(request.Repo, "open", cancellationToken);
        var openPull = openPulls.Find(branch, baseBranch);
        if (openPull is not null)
        {
            var startedChange = new LabelChange(Add: [Labels.Implementing], Remove: [Labels.Implement]);
            await gitBucketClient.ApplyLabelsAsync(request.Repo, issue.Number, startedChange, cancellationToken);
            await gitBucketClient.AddCommentAsync(
                request.Repo,
                issue.Number,
                $"PR #{openPull.Number} from `{branch}` is already open, so I did not start again. " +
                "Push to that PR, or close it and delete the branch to start over.",
                cancellationToken);
            return new JobOutcome("already-open", $"PR #{openPull.Number}");
        }

        var baseRemote = Branches.Remote(baseBranch);
        var branchRemote = Branches.Remote(branch);
        var workspaceRequest = new WorkspaceRequest(
            Repo: request.Repo,
            StartPoint: baseRemote,
            NewBranch: branch,
            ExtraFetch: []);
        var directory = await workspace.PrepareAsync(workspaceRequest, cancellationToken);

        // The commits are only counted when the branch exists.
        var branchExists = await git.RefExistsAsync(directory, branchRemote, cancellationToken);
        var savedCommitCount = branchExists
            ? await git.CountCommitsAsync(directory, baseRemote, branchRemote, cancellationToken)
            : 0;
        if (savedCommitCount > 0)
        {
            // Only the runner's own commits (saved work of an earlier run) are continued;
            // anything else belongs to a person.
            var authors = await git.AuthorsAsync(directory, baseRemote, branchRemote, cancellationToken);
            var hasForeignAuthor = authors.Any(
                author => !string.Equals(author, config.Git.UserEmail, StringComparison.OrdinalIgnoreCase));
            if (hasForeignAuthor)
            {
                throw new JobFailedException(
                    $"Branch `{branch}` already has commits that are not in `{baseBranch}`. Someone may be working on it. " +
                    $"Merge or delete that branch, then remove {Labels.Failed} to retry.");
            }

            var continueRequest = workspaceRequest with { StartPoint = branchRemote };
            directory = await workspace.PrepareAsync(continueRequest, cancellationToken);
            plan += "\n\nEarlier work from a previous run is already on this branch. Continue from it; do not start again.\n";
            run.Info($"continuing from the saved work on {branch}");
        }

        // A sub-issue plan is its own body (the parent thread is not part of it), so only that body is scanned.
        var issueBody = new AttachmentSource("the issue body", issue.Body);
        IReadOnlyList<GbComment> attachmentComments = subIssue is null ? comments : [];
        var attachmentRequest = AttachmentRequest.Of(directory, issueBody, attachmentComments);
        var fetched = await attachmentFetcher.FetchAsync(attachmentRequest, run, cancellationToken);
        plan += fetched.Section;

        var planFile = Path.Combine(directory, "plan.md");
        await File.WriteAllTextAsync(planFile, plan, cancellationToken);
        run.Save("plan.md", plan);

        var implementingChange = LabelChange.Adding(Labels.Implementing);
        await gitBucketClient.ApplyLabelsAsync(request.Repo, issue.Number, implementingChange, cancellationToken);
        await gitBucketClient.AddCommentAsync(
            request.Repo,
            issue.Number,
            $"Copilot started on branch `{branch}` (base `{baseBranch}`).",
            cancellationToken);

        var implementation = new ImplementationRequest(
            Repo: repo,
            Issue: issue,
            Directory: directory,
            BaseBranch: baseBranch,
            Branch: branch,
            Plan: plan)
        {
            Attachments = fetched.ForCopilot,
        };
        try
        {
            return await ImplementAsync(implementation, run, cancellationToken);
        }
        catch
        {
            // Without ai:implementing the planner can start again once a human removes ai:failed.
            await gitBucketClient.RemoveLabelAsync(
                request.Repo, issue.Number, Labels.Implementing, CancellationToken.None);
            throw;
        }
    }

    private async Task<JobOutcome> ImplementAsync(
        ImplementationRequest implementation,
        JobRun run,
        CancellationToken cancellationToken)
    {
        var (repo, issue, directory, baseBranch, branch, plan) = implementation;
        var diffBase = Branches.Remote(baseBranch);
        var task = new AgentTask(
            Agent,
            Workflows.Implement,
            FirstRound: "implement",
            Prompt,
            FixPrompt,
            OutputFile: "PR_BODY.md",
            text => PrBodyParser.Problems(text, plan),
            DiffBase: diffBase)
        {
            Attachments = implementation.Attachments,
        };
        var context = new AgentRunContext(repo, directory, run);
        var result = await session.RunAsync(task, context, cancellationToken);
        if (result.FirstFailure is { } firstFailure)
        {
            // What it wrote is saved to the branch before the job fails.
            var saved = await SaveWorkAsync(implementation, run, cancellationToken);
            throw new JobFailedException(firstFailure.Message + (saved is null
                ? " Copilot had written no files."
                : $" The partial work ({saved.Count} files) is saved on branch `{branch}` (no PR). " +
                  $"Remove {Labels.Failed} to continue from it."));
        }

        var failing = result.Failing;
        var prBody = result.OutputText ?? "(Copilot did not write PR_BODY.md.)";

        // Commit and push first, then judge the size: too big or not, the code is on the branch.
        var stats = await SaveWorkAsync(implementation, run, cancellationToken)
                    ?? throw new JobFailedException("Copilot finished without changing any file.");

        var lines = stats.Sum(diffStat => diffStat.Lines);
        if (!repo.Limits.Allows(stats.Count, lines))
        {
            await gitBucketClient.AddCommentAsync(
                repo.FullName,
                issue.Number,
                $"The change grew beyond \"simple\": {stats.Count} files and {lines} changed lines " +
                $"(limits: {repo.Limits.MaxChangedFiles} files, {repo.Limits.MaxDiffLines} lines). I did not open a PR, " +
                $"but the code is saved on branch `{branch}`. " +
                "Please split the issue (write @ai and ask for a breakdown) or finish it by hand on that branch. " +
                "To start over, delete the branch.",
                cancellationToken);

            var tooBigChange = LabelChange.Removing(Labels.Implement, Labels.Implementing);
            await gitBucketClient.ApplyLabelsAsync(repo.FullName, issue.Number, tooBigChange, cancellationToken);

            return new JobOutcome(
                "too-big", $"{stats.Count} files, {lines} lines, {result.Rounds.Count} copilot calls");
        }

        var openPulls = await gitBucketClient.ListPullsAsync(repo.FullName, "open", cancellationToken);
        var pull = openPulls.Find(branch, baseBranch);

        // Only open a PR when none from this branch exists yet.
        if (pull is null)
        {
            var pullTitle = (failing ? "[WIP - checks failing] " : "") + issue.Title;
            var pullBody = PullBody(issue.Number, prBody, result);
            var newPull = new NewPull(pullTitle, branch, baseBranch, pullBody);
            pull = await gitBucketClient.CreatePullAsync(repo.FullName, newPull, cancellationToken);
        }

        var finishedChange = LabelChange.Removing(Labels.Implement);
        await gitBucketClient.ApplyLabelsAsync(repo.FullName, issue.Number, finishedChange, cancellationToken);
        await gitBucketClient.AddCommentAsync(
            repo.FullName,
            issue.Number,
            $"Copilot opened PR #{pull.Number} from `{branch}` into `{baseBranch}`." +
            (failing ? " Build, test or lint is still failing; the PR is marked WIP." : ""),
            cancellationToken);

        return new JobOutcome(
            failing ? "wip" : "ok",
            $"PR #{pull.Number}, {stats.Count} files, {lines} lines, {result.Rounds.Count} copilot calls");
    }

    /// <summary>Saves a failed or oversized run too, so it cannot lose the work.</summary>
    private Task<IReadOnlyList<DiffStat>?> SaveWorkAsync(
        ImplementationRequest implementation,
        JobRun run,
        CancellationToken cancellationToken)
    {
        var issue = implementation.Issue;
        var message = $"#{issue.Number}: {issue.Title} (Copilot)";
        var work = new WorkToSave(implementation.Directory, message, implementation.Branch);

        return session.SaveWorkAsync(work, run, cancellationToken);
    }

    private async Task<int?> PreviousSubIssueNotDoneAsync(
        string repoName, SubIssueInfo subIssue, CancellationToken cancellationToken)
    {
        if (subIssue.Order <= 1)
        {
            return null;
        }

        var parentComments = await gitBucketClient.ListCommentsAsync(repoName, subIssue.Parent, cancellationToken);
        var subIssueNumbers = SubIssueBody.FindList(parentComments)
            ?? throw new JobFailedException($"Parent #{subIssue.Parent} has no sub-issue list; cannot check the order.");
        if (subIssueNumbers.Count < subIssue.Order - 1)
        {
            throw new JobFailedException(
                $"Parent #{subIssue.Parent} lists {subIssueNumbers.Count} sub-issues; " +
                $"this one says it is number {subIssue.Order}.");
        }

        var previous = await gitBucketClient.GetIssueAsync(
            repoName, subIssueNumbers[subIssue.Order - 2], cancellationToken);
        var pulls = await gitBucketClient.ListPullsAsync(repoName, "closed", cancellationToken);

        var previousSlot = new SubIssueSlot(subIssue.Parent, subIssue.Order - 1);
        var isPreviousDone = SubIssueBody.IsDone(previous, previousSlot, pulls);

        return isPreviousDone ? null : previous.Number;
    }

    private async Task<JobOutcome> RejectAsync(
        string repoName, GbIssue issue, string reason, CancellationToken cancellationToken)
    {
        await gitBucketClient.AddCommentAsync(repoName, issue.Number, $"I did not start: {reason}", cancellationToken);
        await gitBucketClient.RemoveLabelAsync(repoName, issue.Number, Labels.Implement, cancellationToken);
        return new JobOutcome("rejected", reason);
    }

    private static string PullBody(int number, string prBody, AgentResult result)
    {
        var builder = new StringBuilder();
        if (result.Failing)
        {
            builder.AppendLine("**WIP: build, test or lint is still failing after Copilot stopped.** See the check output below.")
                .AppendLine();
        }

        var trimmedPrBody = prBody.Trim();
        builder.AppendLine(trimmedPrBody).AppendLine();
        AgentSession.AppendReport(builder, result, "PR description problems");
        builder.AppendLine().AppendLine($"Closes #{number}");

        return builder.ToString();
    }
}
