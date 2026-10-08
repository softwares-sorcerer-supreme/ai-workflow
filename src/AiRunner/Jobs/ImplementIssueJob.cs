using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

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
    IGitBucketClient gitBucket,
    IWorkspace workspace,
    IGitClient git,
    AgentSession session,
    IStateStore state) : IJob
{
    public const string Agent = "implement-issue";
    // The stages and the follow-up procedure live only in agents/implement-issue.agent.md.
    public const string Prompt =
        "Implement the plan in plan.md. Follow the implement-issue agent rules and AGENTS.md; finish every stage before you stop.";
    public const string FixPrompt =
        "Follow-up round: read fix.md and follow the Follow-up round section of the implement-issue agent rules.";

    public JobType Type => JobType.ImplementIssue;

    public JobInfo Info => new(Workflows.Implement, "Implementation", AgentSession.JobTimeout(config));

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken ct)
    {
        var (repo, issue, comments, decision) = await gitBucket.LoadIssueAsync(config, state, request, ct);
        if (decision.Job?.Type != Type)
            return JobOutcome.NotNeeded(decision.Reason);

        if (JobPlanner.WhyNotImplement(issue.LabelNames()) is { } why)
            return await RejectAsync(request.Repo, issue, why, ct);

        // Base branch, work branch and plan: a sub-issue builds on the parent's feature branch.
        var sub = issue.HasLabel(Labels.SubIssue)
            ? JobSupport.Parse(() => SubIssueBody.Parse(issue.Body), "The sub-issue body")
            : null;
        string baseBranch, branch, plan;
        if (sub is not null)
        {
            var waitingFor = await PreviousSubIssueNotDoneAsync(request.Repo, sub, ct);
            if (waitingFor is not null)
                return await RejectAsync(request.Repo, issue,
                    $"Sub-issues are built in order. #{waitingFor} (#{sub.Parent}.{sub.Order - 1}) is not merged yet; " +
                    $"I add `{Labels.Implement}` here automatically when it is.", ct);
            baseBranch = sub.BaseBranch;
            branch = Branches.SubIssue(sub.Parent, sub.Order);
            plan = ContextBuilder.PlanFromSubIssue(issue);
        }
        else
        {
            var spec = JobSupport.LatestBlock(comments, Markers.Spec)
                ?? throw new JobFailedException("No SPEC comment on this issue. Run the analysis until Status is ready.");
            baseBranch = await gitBucket.DefaultBranchAsync(request.Repo, ct);
            branch = Branches.Issue(issue.Number);
            plan = ContextBuilder.PlanFromSpec(issue, spec);
        }
        if (branch == baseBranch)
            throw new JobFailedException($"Refusing to work on the base branch {baseBranch}.");

        // A PR from this branch is already open (e.g. an earlier run stopped right after opening it).
        var open = (await gitBucket.ListPullsAsync(request.Repo, "open", ct)).Find(branch, baseBranch);
        if (open is not null)
        {
            await gitBucket.ApplyLabelsAsync(request.Repo, issue.Number, [Labels.Implementing], [Labels.Implement], ct);
            await gitBucket.AddCommentAsync(request.Repo, issue.Number,
                $"PR #{open.Number} from `{branch}` is already open, so I did not start again. " +
                "Push to that PR, or close it and delete the branch to start over.", ct);
            return new JobOutcome("already-open", $"PR #{open.Number}");
        }

        var dir = await workspace.PrepareAsync(request.Repo, Branches.Remote(baseBranch), branch, [], ct);
        if (await git.RefExistsAsync(dir, Branches.Remote(branch), ct) &&
            await git.CountCommitsAsync(dir, Branches.Remote(baseBranch), Branches.Remote(branch), ct) > 0)
        {
            // Only the runner's own commits (saved work of an earlier run) are continued; anything else belongs to a person.
            var authors = await git.AuthorsAsync(dir, Branches.Remote(baseBranch), Branches.Remote(branch), ct);
            if (authors.Any(a => !string.Equals(a, config.Git.UserEmail, StringComparison.OrdinalIgnoreCase)))
                throw new JobFailedException(
                    $"Branch `{branch}` already has commits that are not in `{baseBranch}`. Someone may be working on it. " +
                    $"Merge or delete that branch, then remove {Labels.Failed} to retry.");
            dir = await workspace.PrepareAsync(request.Repo, Branches.Remote(branch), branch, [], ct);
            plan += "\n\nEarlier work from a previous run is already on this branch. Continue from it; do not start again.\n";
            run.Info($"continuing from the saved work on {branch}");
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "plan.md"), plan, ct);
        run.Save("plan.md", plan);

        await gitBucket.ApplyLabelsAsync(request.Repo, issue.Number, [Labels.Implementing], [], ct);
        await gitBucket.AddCommentAsync(request.Repo, issue.Number,
            $"Copilot started on branch `{branch}` (base `{baseBranch}`).", ct);
        try
        {
            return await ImplementAsync(request.Repo, repo, issue, dir, baseBranch, branch, plan, run, ct);
        }
        catch
        {
            // Without ai:implementing the planner can start again once a human removes ai:failed.
            await gitBucket.RemoveLabelAsync(request.Repo, issue.Number, Labels.Implementing, CancellationToken.None);
            throw;
        }
    }

    private async Task<JobOutcome> ImplementAsync(string repoName, RepoConfig repo, GbIssue issue, string dir,
        string baseBranch, string branch, string plan, JobRun run, CancellationToken ct)
    {
        var task = new AgentTask(Agent, Workflows.Implement, FirstRound: "implement", Prompt, FixPrompt, OutputFile: "PR_BODY.md",
            text => PrBodyParser.Problems(text, plan), DiffBase: Branches.Remote(baseBranch));
        var result = await session.RunAsync(task, repoName, repo, dir, run, ct);
        if (result.FirstFailure is { } firstFailure)
        {
            // What it wrote is saved to the branch before the job fails.
            var saved = await SaveWorkAsync(dir, issue, branch, run, ct);
            throw new JobFailedException(firstFailure.Message + (saved is null
                ? " Copilot had written no files."
                : $" The partial work ({saved.Count} files) is saved on branch `{branch}` (no PR). " +
                  $"Remove {Labels.Failed} to continue from it."));
        }
        var failing = result.Failing;
        var prBody = result.OutputText ?? "(Copilot did not write PR_BODY.md.)";

        // Commit and push first, then judge the size: too big or not, the code is on the branch.
        var stats = await SaveWorkAsync(dir, issue, branch, run, ct)
                    ?? throw new JobFailedException("Copilot finished without changing any file.");

        var lines = stats.Sum(s => s.Lines);
        if (!repo.Limits.Allows(stats.Count, lines))
        {
            await gitBucket.AddCommentAsync(repoName, issue.Number,
                $"The change grew beyond \"simple\": {stats.Count} files and {lines} changed lines " +
                $"(limits: {repo.Limits.MaxChangedFiles} files, {repo.Limits.MaxDiffLines} lines). I did not open a PR, " +
                $"but the code is saved on branch `{branch}`. " +
                "Please split the issue (write @ai and ask for a breakdown) or finish it by hand on that branch. " +
                "To start over, delete the branch.", ct);
            await gitBucket.ApplyLabelsAsync(repoName, issue.Number, [], [Labels.Implement, Labels.Implementing], ct);
            return new JobOutcome("too-big", $"{stats.Count} files, {lines} lines, {result.Rounds.Count} copilot calls");
        }

        var pull = (await gitBucket.ListPullsAsync(repoName, "open", ct)).Find(branch, baseBranch)
                   ?? await gitBucket.CreatePullAsync(repoName,
                       (failing ? "[WIP - checks failing] " : "") + issue.Title,
                       branch, baseBranch, PullBody(issue.Number, prBody, result), ct);

        await gitBucket.ApplyLabelsAsync(repoName, issue.Number, [], [Labels.Implement], ct);
        await gitBucket.AddCommentAsync(repoName, issue.Number,
            $"Copilot opened PR #{pull.Number} from `{branch}` into `{baseBranch}`." +
            (failing ? " Build, test or lint is still failing; the PR is marked WIP." : ""), ct);
        return new JobOutcome(failing ? "wip" : "ok",
            $"PR #{pull.Number}, {stats.Count} files, {lines} lines, {result.Rounds.Count} copilot calls");
    }

    /// <summary>Saves a failed or oversized run too, so it cannot lose the work.</summary>
    private Task<IReadOnlyList<DiffStat>?> SaveWorkAsync(string dir, GbIssue issue, string branch, JobRun run, CancellationToken ct) =>
        session.SaveWorkAsync(dir, $"#{issue.Number}: {issue.Title} (Copilot)", branch, run, ct);

    private async Task<int?> PreviousSubIssueNotDoneAsync(string repoName, SubIssueInfo sub, CancellationToken ct)
    {
        if (sub.Order <= 1) return null;
        var parentComments = await gitBucket.ListCommentsAsync(repoName, sub.Parent, ct);
        var list = SubIssueBody.FindList(parentComments)
            ?? throw new JobFailedException($"Parent #{sub.Parent} has no sub-issue list; cannot check the order.");
        if (list.Count < sub.Order - 1)
            throw new JobFailedException($"Parent #{sub.Parent} lists {list.Count} sub-issues; this one says it is number {sub.Order}.");

        var previous = await gitBucket.GetIssueAsync(repoName, list[sub.Order - 2], ct);
        var pulls = await gitBucket.ListPullsAsync(repoName, "closed", ct);
        return SubIssueBody.IsDone(previous, sub.Parent, sub.Order - 1, pulls) ? null : previous.Number;
    }

    private async Task<JobOutcome> RejectAsync(string repoName, GbIssue issue, string why, CancellationToken ct)
    {
        await gitBucket.AddCommentAsync(repoName, issue.Number, $"I did not start: {why}", ct);
        await gitBucket.RemoveLabelAsync(repoName, issue.Number, Labels.Implement, ct);
        return new JobOutcome("rejected", why);
    }

    private static string PullBody(int number, string prBody, AgentResult result)
    {
        var sb = new StringBuilder();
        if (result.Failing)
            sb.AppendLine("**WIP: build, test or lint is still failing after Copilot stopped.** See the check output below.").AppendLine();
        sb.AppendLine(prBody.Trim()).AppendLine();
        AgentSession.AppendReport(sb, result, "PR description problems");
        sb.AppendLine().AppendLine($"Closes #{number}");
        return sb.ToString();
    }
}
