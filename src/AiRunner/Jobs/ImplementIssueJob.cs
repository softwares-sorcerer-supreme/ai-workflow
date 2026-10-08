using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// Workflow 2: a human added ai-implement on a simple issue or sub-issue. Copilot writes the
/// code on a branch; the runner checks build and tests, enforces the size limits, commits,
/// pushes and opens the PR. The agent never runs git and never merges.
/// </summary>
public sealed class ImplementIssueJob(
    RunnerConfig config,
    IGitBucketClient gitBucket,
    IWorkspace workspace,
    IGitClient git,
    ICopilotRunner copilot,
    IProcessRunner processes,
    IStateStore state) : IJob
{
    public const string Agent = "implement-issue";
    public const string Prompt =
        "Implement the plan in plan.md. Follow the implement-issue agent rules and AGENTS.md. Write PR_BODY.md when done.";

    public JobType Type => JobType.ImplementIssue;

    // Copilot, then the setup, build and test commands (each up to the same timeout).
    public JobInfo Info => new(Workflows.Implement, "Implementation", TimeSpan.FromMinutes(config.Copilot.ImplementTimeoutMinutes * 2 + 10));

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
            throw new JobFailedException(
                $"Branch `{branch}` already has commits that are not in `{baseBranch}`. Someone may be working on it. " +
                $"Merge or delete that branch, then remove {Labels.Failed} to retry.");

        await File.WriteAllTextAsync(Path.Combine(dir, "plan.md"), plan, ct);
        run.Save("plan.md", plan);

        await gitBucket.ApplyLabelsAsync(request.Repo, issue.Number, [Labels.Implementing], [], ct);
        await gitBucket.AddCommentAsync(request.Repo, issue.Number,
            $"Copilot started on branch `{branch}` (base `{baseBranch}`).", ct);
        try
        {
            return await ImplementAsync(request.Repo, repo, issue, dir, baseBranch, branch, run, ct);
        }
        catch
        {
            // Without ai:implementing the planner can start again once a human removes ai:failed.
            await gitBucket.RemoveLabelAsync(request.Repo, issue.Number, Labels.Implementing, CancellationToken.None);
            throw;
        }
    }

    private async Task<JobOutcome> ImplementAsync(string repoName, RepoConfig repo, GbIssue issue, string dir,
        string baseBranch, string branch, JobRun run, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(config.Copilot.ImplementTimeoutMinutes);
        async Task<ProcessResult> RunCommandAsync(string command, string logName)
        {
            var result = await processes.RunAsync(ProcessSpec.Shell(command, dir, timeout), ct);
            run.Save(logName, result.Report(command));
            run.Info($"'{command}': exit {result.ExitCode}");
            return result;
        }

        // Dependencies first: the work folder was cleaned, so node_modules, .venv etc. are gone.
        if (!string.IsNullOrWhiteSpace(repo.Commands.Setup))
        {
            var setup = await RunCommandAsync(repo.Commands.Setup.Trim(), "setup.txt");
            if (!setup.Ok)
                throw new JobFailedException($"The setup command `{repo.Commands.Setup}` failed (exit {setup.ExitCode}): {setup.Tail(600)}");
        }

        var gitConfig = git.ConfigFingerprint(dir);
        await copilot.RunCopilotAsync(
            new CopilotRequest(dir, Prompt, Agent, repo.ModelFor(Workflows.Implement), timeout)
            {
                AllowTools = ["write", .. repo.Commands.AllowedForAgent().Select(c => $"shell({c})"),
                              .. repo.Commands.AllowShell.Select(p => $"shell({p})")],
                DenyTools = ["shell(git)", "shell(git:*)"],
            }, run, ct);

        // The runner checks build and tests itself; the agent's own claim is not enough.
        var checks = new List<(string Command, ProcessResult Result)>();
        foreach (var command in repo.Commands.Verification())
        {
            var result = await RunCommandAsync(command, $"check-{checks.Count + 1}.txt");
            checks.Add((command, result));
            if (!result.Ok) break;
        }
        var failing = checks.Any(c => !c.Result.Ok);

        // The agent and the checks ran code in this clone. If .git/config changed, git itself could be
        // made to run that code or send the token elsewhere: run no more git here, throw the clone away.
        if (git.ConfigFingerprint(dir) != gitConfig)
        {
            workspace.Discard(repoName);
            throw new JobFailedException("Something in the work folder changed .git/config during the run. " +
                                         "I deleted the clone and pushed nothing. Please check the agent output in the log.");
        }

        var prBodyFile = Path.Combine(dir, "PR_BODY.md");
        run.SaveCopy(prBodyFile, "PR_BODY.md");
        var prBody = File.Exists(prBodyFile) ? await File.ReadAllTextAsync(prBodyFile, ct) : "(Copilot did not write PR_BODY.md.)";

        var stats = await git.StageAllAsync(dir, ct);
        run.Save("numstat.txt", string.Join("\n", stats.Select(s => $"{s.Added}\t{s.Deleted}\t{s.Path}")));
        if (stats.Count == 0)
            throw new JobFailedException("Copilot finished without changing any file.");

        var lines = stats.Sum(s => s.Lines);
        if (!repo.Limits.Allows(stats.Count, lines))
        {
            await gitBucket.AddCommentAsync(repoName, issue.Number,
                $"The change grew beyond \"simple\": {stats.Count} files and {lines} changed lines " +
                $"(limits: {repo.Limits.MaxChangedFiles} files, {repo.Limits.MaxDiffLines} lines). I did not open a PR. " +
                $"Please split the issue (write @ai and ask for a breakdown) or build it by hand.", ct);
            await gitBucket.ApplyLabelsAsync(repoName, issue.Number, [], [Labels.Implement, Labels.Implementing], ct);
            return new JobOutcome("too-big", $"{stats.Count} files, {lines} lines");
        }

        await git.CommitAsync(dir, $"#{issue.Number}: {issue.Title} (Copilot)", ct);
        await git.PushAsync(dir, "HEAD", branch, ct);
        run.Info($"pushed {branch}: {stats.Count} files, {lines} lines");

        var pull = (await gitBucket.ListPullsAsync(repoName, "open", ct)).Find(branch, baseBranch)
                   ?? await gitBucket.CreatePullAsync(repoName,
                       (failing ? "[WIP - tests failing] " : "") + issue.Title,
                       branch, baseBranch, PullBody(issue.Number, prBody, checks, failing), ct);

        await gitBucket.ApplyLabelsAsync(repoName, issue.Number, [], [Labels.Implement], ct);
        await gitBucket.AddCommentAsync(repoName, issue.Number,
            $"Copilot opened PR #{pull.Number} from `{branch}` into `{baseBranch}`." +
            (failing ? " Build or tests are still failing; the PR is marked WIP." : ""), ct);
        return new JobOutcome(failing ? "wip" : "ok", $"PR #{pull.Number}, {stats.Count} files, {lines} lines");
    }

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

    private static string PullBody(int number, string prBody, List<(string Command, ProcessResult Result)> checks, bool failing)
    {
        var sb = new StringBuilder();
        if (failing)
            sb.AppendLine("**WIP: build or tests are still failing after Copilot stopped.** See the check output below.").AppendLine();
        sb.AppendLine(prBody.Trim()).AppendLine();
        sb.AppendLine("### Checks run by ai-runner");
        if (checks.Count == 0) sb.AppendLine("None configured (repos.yml `commands`).");
        foreach (var (command, result) in checks)
            sb.AppendLine($"- `{command}`: {(result.Ok ? "passed" : result.TimedOut ? "timed out" : $"failed (exit {result.ExitCode})")}");
        var failed = checks.FirstOrDefault(c => !c.Result.Ok);
        if (failed.Result is not null)
            sb.AppendLine().AppendLine("```").AppendLine(failed.Result.Tail(2000)).AppendLine("```");
        sb.AppendLine().AppendLine($"Closes #{number}");
        return sb.ToString();
    }
}
