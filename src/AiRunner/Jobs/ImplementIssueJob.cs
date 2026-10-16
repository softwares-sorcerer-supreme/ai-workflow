using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>What ImplementIssueJob works on once it has an issue, its branches and the project folder.</summary>
/// <param name="Target">The component the code is changed in; its repo gets the branch and the PR.</param>
/// <param name="IssueRepo">The repo that holds the issue: the project's issues repo.</param>
/// <param name="BaseBranch">The branch the PR goes into.</param>
/// <param name="Branch">The branch the work is pushed to.</param>
/// <param name="Plan">The text of plan.md.</param>
public sealed record ImplementationRequest(
    ProjectConfig Project,
    ComponentConfig Target,
    string IssueRepo,
    GbIssue Issue,
    PreparedWorkspace Workspace,
    string BaseBranch,
    string Branch,
    string Plan)
{
    /// <summary>Files for the Copilot --attachment flag (round 1 only).</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>The plan whose acceptance criteria the PR description must list: the issue's own.</summary>
    public string CriteriaPlan { get; init; } = "";
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
        JobSupport.RulesPrefix +
        "Implement the plan in plan.md. Follow the implement-issue agent rules; finish every stage before you stop.";
    public const string FixPrompt =
        JobSupport.RulesPrefix +
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
        var (project, issue, comments, decision) = await jobLoader.LoadIssueAsync(request, cancellationToken);
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

        // The issue is in the issues repo; the code, the branch and the PR are in the target component's repo.
        // A sub-issue names its component; a simple issue gets it from its analysis.
        var subIssue = issue.HasLabel(Labels.SubIssue)
            ? JobSupport.Parse(() => SubIssueBody.Parse(issue.Body), "The sub-issue body")
            : null;
        var target = subIssue is not null
            ? project.TargetComponent(subIssue.Component)
            : project.TargetOfIssue(comments);
        var targetRepo = target.Repo;

        // Base branch, work branch and plan: a sub-issue builds on the parent's feature branch.
        // The criteria plan is the text the PR description is checked against: the issue's own, never the parent's.
        string baseBranch, branch, plan, criteriaPlan;
        if (subIssue is not null)
        {
            // The parent's comments give the order of the sub-issues and the SPEC that every sub-issue shares.
            var parentComments = await gitBucketClient.ListCommentsAsync(
                project.IssuesRepo, subIssue.Parent, cancellationToken);
            var waitingFor = await PreviousSubIssueNotDoneAsync(project, subIssue, parentComments, cancellationToken);
            if (waitingFor is not null)
            {
                var waitingReason =
                    $"Sub-issues are built in order. #{waitingFor} (#{subIssue.Parent}.{subIssue.Order - 1}) " +
                    $"is not merged yet; I add `{Labels.Implement}` here automatically when it is.";

                return await RejectAsync(request.Repo, issue, waitingReason, cancellationToken);
            }

            baseBranch = subIssue.BaseBranch;
            branch = Branches.SubIssue(subIssue.Parent, subIssue.Order);
            var parentSpec = JobSupport.LatestBlock(parentComments, Markers.Spec);
            plan = ContextBuilder.PlanFromSubIssue(issue, subIssue.Parent, parentSpec);
            criteriaPlan = ContextBuilder.PlanFromSubIssue(issue, null, null);
        }
        else
        {
            var spec = JobSupport.LatestBlock(comments, Markers.Spec)
                ?? throw new JobFailedException("No SPEC comment on this issue. Run the analysis until Status is ready.");
            baseBranch = await gitBucketClient.DefaultBranchAsync(targetRepo, cancellationToken);
            branch = Branches.Issue(issue.Number);
            plan = ContextBuilder.PlanFromSpec(issue, spec);
            criteriaPlan = plan;
        }

        if (branch == baseBranch)
        {
            throw new JobFailedException($"Refusing to work on the base branch {baseBranch}.");
        }

        // A PR from this branch is already open (e.g. an earlier run stopped right after opening it).
        var openPulls = await gitBucketClient.ListPullsAsync(targetRepo, "open", cancellationToken);
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
            Project: project,
            TargetRepo: targetRepo,
            StartPoint: baseRemote,
            NewBranch: branch,
            ExtraFetch: [])
        {
            Writable = true,
            TargetComponent = target.Name,
            OthersAt = subIssue?.BaseBranch,
        };
        var prepared = await workspace.PrepareAsync(workspaceRequest, cancellationToken);
        var clone = prepared.TargetClone;

        // The commits are only counted when the branch exists.
        var branchExists = await git.RefExistsAsync(clone, branchRemote, cancellationToken);
        var savedCommitCount = branchExists
            ? await git.CountCommitsAsync(clone, baseRemote, branchRemote, cancellationToken)
            : 0;
        if (savedCommitCount > 0)
        {
            // Only the runner's own commits (saved work of an earlier run) are continued;
            // anything else belongs to a person.
            var authors = await git.AuthorsAsync(clone, baseRemote, branchRemote, cancellationToken);
            var hasForeignAuthor = authors.Any(
                author => !string.Equals(author, config.Git.UserEmail, StringComparison.OrdinalIgnoreCase));
            if (hasForeignAuthor)
            {
                throw new JobFailedException(
                    $"Branch `{branch}` already has commits that are not in `{baseBranch}`. Someone may be working on it. " +
                    $"Merge or delete that branch, then remove {Labels.Failed} to retry.");
            }

            var continueRequest = workspaceRequest with { StartPoint = branchRemote };
            prepared = await workspace.PrepareAsync(continueRequest, cancellationToken);
            plan += "\n\nEarlier work from a previous run is already on this branch. Continue from it; do not start again.\n";
            run.Info($"continuing from the saved work on {branch}");
        }

        // A sub-issue plan is its own body (the parent thread is not part of it), so only that body is scanned.
        var issueBody = new AttachmentSource("the issue body", issue.Body);
        IReadOnlyList<GbComment> attachmentComments = subIssue is null ? comments : [];
        var attachmentRequest = AttachmentRequest.Of(prepared.Root, issueBody, attachmentComments);
        var fetched = await attachmentFetcher.FetchAsync(attachmentRequest, run, cancellationToken);
        plan += fetched.Section;

        plan += JobSupport.WhereToWorkSection([target], prepared);
        var planFile = Path.Combine(prepared.Root, "plan.md");
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
            Project: project,
            Target: target,
            IssueRepo: request.Repo,
            Issue: issue,
            Workspace: prepared,
            BaseBranch: baseBranch,
            Branch: branch,
            Plan: plan)
        {
            Attachments = fetched.ForCopilot,
            CriteriaPlan = criteriaPlan,
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
        var (project, target, issueRepo, issue, prepared, baseBranch, branch, plan) = implementation;
        var settings = project.Settings;
        var diffBase = Branches.Remote(baseBranch);
        var task = new AgentTask(
            Agent,
            Workflows.Implement,
            FirstRound: "implement",
            Prompt,
            FixPrompt,
            OutputFile: "PR_BODY.md",
            text => PrBodyParser.Problems(text, implementation.CriteriaPlan),
            DiffBase: diffBase)
        {
            Attachments = implementation.Attachments,
        };
        var context = new AgentRunContext(project, target, prepared, run);
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
        if (!settings.Limits.Allows(stats.Count, lines))
        {
            var isSubIssue = issue.HasLabel(Labels.SubIssue);
            var tooBigAdvice = isSubIssue
                ? $"A sub-issue cannot be split again. Finish it by hand on that branch (open a PR from `{branch}` into " +
                  $"`{baseBranch}`), or delete the branch to start over, then remove `{Labels.Failed}`."
                : $"Please split the issue (write {settings.TriggerKeyword} and ask for a breakdown) " +
                  "or finish it by hand on that branch. To start over, delete the branch.";
            await gitBucketClient.AddCommentAsync(
                issueRepo,
                issue.Number,
                $"The change grew beyond \"simple\": {stats.Count} files and {lines} changed lines " +
                $"(limits: {settings.Limits.MaxChangedFiles} files, {settings.Limits.MaxDiffLines} lines). " +
                $"I did not open a PR, but the code is saved on branch `{branch}`. " + tooBigAdvice,
                cancellationToken);

            // Without ai:failed the sequencer would start this sub-issue again on the next poll, and so on forever.
            var tooBigChange = new LabelChange(
                Add: isSubIssue ? [Labels.Failed] : [],
                Remove: [Labels.Implement, Labels.Implementing]);
            await gitBucketClient.ApplyLabelsAsync(issueRepo, issue.Number, tooBigChange, cancellationToken);

            return new JobOutcome(
                "too-big", $"{stats.Count} files, {lines} lines, {result.Rounds.Count} copilot calls");
        }

        var openPulls = await gitBucketClient.ListPullsAsync(target.Repo, "open", cancellationToken);
        var pull = openPulls.Find(branch, baseBranch);

        // Only open a PR when none from this branch exists yet.
        if (pull is null)
        {
            var pullTitle = (failing ? "[WIP - checks failing] " : "") + issue.Title;
            var issueRef = new IssueRef(issueRepo, issue.Number);
            var pullBody = PullBody(issueRef, prBody, result);
            var newPull = new NewPull(pullTitle, branch, baseBranch, pullBody);
            pull = await gitBucketClient.CreatePullAsync(target.Repo, newPull, cancellationToken);
        }

        var finishedChange = LabelChange.Removing(Labels.Implement);
        await gitBucketClient.ApplyLabelsAsync(issueRepo, issue.Number, finishedChange, cancellationToken);
        await gitBucketClient.AddCommentAsync(
            issueRepo,
            issue.Number,
            $"Copilot opened PR {JobSupport.PullRef(pull.Number, issueRepo, target.Repo)} from `{branch}` into `{baseBranch}`." +
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
        var work = new WorkToSave(implementation.Workspace.TargetClone, message, implementation.Branch);

        return session.SaveWorkAsync(work, run, cancellationToken);
    }

    /// <summary>
    /// The number of the previous sub-issue when it is not done yet, else null. Its PR is in the repo of its own
    /// component, which can differ from the repo of this sub-issue.
    /// </summary>
    private async Task<int?> PreviousSubIssueNotDoneAsync(
        ProjectConfig project,
        SubIssueInfo subIssue,
        IReadOnlyList<GbComment> parentComments,
        CancellationToken cancellationToken)
    {
        if (subIssue.Order <= 1)
        {
            return null;
        }

        var repoName = project.IssuesRepo;
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
        if (!previous.IsOpen())
        {
            return null;
        }

        var previousInfo = JobSupport.Parse(() => SubIssueBody.Parse(previous.Body), "The previous sub-issue body");
        var previousRepo = project.TargetComponent(previousInfo?.Component).Repo;
        var pulls = await gitBucketClient.ListPullsAsync(previousRepo, "closed", cancellationToken);

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

    private static string PullBody(IssueRef issue, string prBody, AgentResult result)
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

        // The agent is told not to write "Closes #n", but nothing else stops it. GitBucket could then close an
        // unrelated issue with that number in the PR's repo, so the keyword is neutralised in everything the agent
        // or the checks wrote. The runner's own line below comes after, so it is never touched.
        var safeText = CloseKeywords.Neutralize(builder.ToString());

        // Never "Closes": GitBucket would not close an issue in another repo, and the runner closes the issue itself.
        var issueLine = $"Issue: {issue.Repo}#{issue.Number}";

        return safeText + Environment.NewLine + issueLine + Environment.NewLine;
    }
}
