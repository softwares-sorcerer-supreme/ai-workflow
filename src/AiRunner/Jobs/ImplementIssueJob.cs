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
    ICopilotRunner copilot,
    IProcessRunner processes,
    IStateStore state) : IJob
{
    public const string Agent = "implement-issue";
    // The stages and the follow-up procedure live only in agents/implement-issue.agent.md.
    public const string Prompt =
        "Implement the plan in plan.md. Follow the implement-issue agent rules and AGENTS.md; finish every stage before you stop.";
    public const string FixPrompt =
        "Follow-up round: read fix.md and follow the Follow-up round section of the implement-issue agent rules.";

    public JobType Type => JobType.ImplementIssue;

    // Setup, then per round Copilot and the checks (each up to the same timeout); the repo with the most fix rounds counts.
    public JobInfo Info => new(Workflows.Implement, "Implementation",
        TimeSpan.FromMinutes(config.Copilot.ImplementTimeoutMinutes * (2 + 2 * MaxFixRounds) + 10));

    private int MaxFixRounds => config.Repos.Select(r => Math.Max(0, r.Implement.MaxFixRounds)).DefaultIfEmpty(0).Max();

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
                throw new JobFailedException($"The setup command `{repo.Commands.Setup}` {Status(setup)}: {setup.Tail(600)}");
        }

        var gitConfig = git.ConfigFingerprint(dir);

        // The agent and the checks run code in this clone. If .git/config changed, git itself could be
        // made to run that code or send the token elsewhere: run no more git here, throw the clone away.
        void GuardGitConfig()
        {
            if (git.ConfigFingerprint(dir) == gitConfig) return;
            workspace.Discard(repoName);
            throw new JobFailedException("Something in the work folder changed .git/config during the run. " +
                                         "I deleted the clone and pushed nothing. Please check the agent output in the log.");
        }

        // Returns the failure instead of throwing: round 1 fails the job, a failed fix round still opens the PR.
        var model = repo.ModelFor(Workflows.Implement);
        var allowTools = AllowTools(repo.Commands);
        async Task<JobFailedException?> RunAgentAsync(string prompt, int round)
        {
            JobFailedException? failure = null;
            try
            {
                await copilot.RunCopilotAsync(
                    new CopilotRequest(dir, prompt, Agent, model, timeout)
                    {
                        AllowTools = allowTools,
                        DenyTools = DenyTools,
                        TranscriptFile = Path.Combine(run.Dir, $"round-{round}-transcript.md"),
                    }, run, ct, $"round-{round}-copilot");
            }
            catch (JobFailedException e)
            {
                failure = e;
            }
            GuardGitConfig();
            return failure;
        }

        // Build, then test and lint. After a failed build the rest would only repeat its errors; test
        // and lint both run, so one fix round gets every failure at once.
        async Task<List<(string Command, ProcessResult Result)>> RunChecksAsync(int round)
        {
            var checks = new List<(string Command, ProcessResult Result)>();
            var buildFirst = !string.IsNullOrWhiteSpace(repo.Commands.Build);   // Verification() starts with Build
            foreach (var command in repo.Commands.Verification())
            {
                var result = await RunCommandAsync(command, $"round-{round}-check-{checks.Count + 1}.txt");
                checks.Add((command, result));
                if (!result.Ok && buildFirst && checks.Count == 1) break;
            }
            GuardGitConfig();
            return checks;
        }

        var prBodyFile = Path.Combine(dir, "PR_BODY.md");

        // The agent verifies, reviews and writes PR_BODY.md itself. The runner checks the result on its
        // own (the agent's claim is not enough) and, while fix rounds are left, sends back what is wrong.
        if (await RunAgentAsync(Prompt, 1) is { } firstFailure)
        {
            // What it wrote is saved to the branch before the job fails.
            var saved = await SaveWorkAsync(dir, issue, branch, run, ct);
            throw new JobFailedException(firstFailure.Message + (saved is null
                ? " Copilot had written no files."
                : $" The partial work ({saved.Count} files) is saved on branch `{branch}` (no PR). " +
                  $"Remove {Labels.Failed} to continue from it."));
        }

        var rounds = new List<string>();
        string? fixFailure = null;
        string? diffBeforeFix = null;
        List<(string Command, ProcessResult Result)> checks = [];
        string? prBodyText;
        IReadOnlyList<string> prBodyProblems;
        for (var round = 1; ; round++)
        {
            // A fix round that changed no code (only PR_BODY.md, or Copilot failed early) keeps the checks it had.
            if (diffBeforeFix is null || await git.StageAndDiffAsync(dir, Branches.Remote(baseBranch), ct) != diffBeforeFix)
                checks = await RunChecksAsync(round);
            else
                run.Info($"round {round}: no code changed, the checks of round {round - 1} still hold");
            prBodyText = File.Exists(prBodyFile) ? await File.ReadAllTextAsync(prBodyFile, ct) : null;
            prBodyProblems = PrBodyParser.Problems(prBodyText, plan);
            rounds.Add($"Round {round} ({(round == 1 ? "implement" : "fix")}): " +
                       (fixFailure is null ? "" : $"Copilot failed ({fixFailure}); ") +
                       $"{ChecksSummary(checks)}; " +
                       $"PR_BODY.md {(prBodyProblems.Count == 0 ? "complete" : $"{prBodyProblems.Count} problem(s)")}");

            var timeLeft = ImplementGate.TimeForAnotherRound(run.TimeLeft, timeout);
            var step = ImplementGate.Decide(checks.All(c => c.Result.Ok), prBodyProblems.Count, round - 1,
                repo.Implement.MaxFixRounds, canRunAgain: fixFailure is null && timeLeft);
            if (step != ImplementStep.Fix)
            {
                if (step == ImplementStep.OutOfRounds && !timeLeft)
                    run.Info($"round {round}: too little job time left for another fix round");
                break;
            }

            var next = round + 1;
            var failures = checks.Where(c => !c.Result.Ok)
                .Select(c => new CheckFailure(c.Command, Status(c.Result), c.Result.Tail(3000)))
                .ToList();
            var fix = ContextBuilder.ForFix(next, failures, prBodyProblems);
            diffBeforeFix = await git.StageAndDiffAsync(dir, Branches.Remote(baseBranch), ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "fix.md"), fix, ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "diff.patch"), diffBeforeFix, ct);
            run.Save($"fix-{next}.md", fix);
            run.Info($"{rounds[^1]}; starting fix round {next}");
            if (await RunAgentAsync(FixPrompt, next) is { } failed)
            {
                // Often the quota or the timeout: no more rounds, but what exists still becomes a PR.
                fixFailure = Text.Short(failed.Message, 200);
                run.Info($"fix round {next}: {fixFailure}");
            }
        }
        var failing = checks.Any(c => !c.Result.Ok);

        run.SaveCopy(prBodyFile, "PR_BODY.md");
        var prBody = prBodyText ?? "(Copilot did not write PR_BODY.md.)";

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
            return new JobOutcome("too-big", $"{stats.Count} files, {lines} lines, {rounds.Count} copilot calls");
        }

        var pull = (await gitBucket.ListPullsAsync(repoName, "open", ct)).Find(branch, baseBranch)
                   ?? await gitBucket.CreatePullAsync(repoName,
                       (failing ? "[WIP - checks failing] " : "") + issue.Title,
                       branch, baseBranch, PullBody(issue.Number, prBody, checks, failing, rounds, prBodyProblems), ct);

        await gitBucket.ApplyLabelsAsync(repoName, issue.Number, [], [Labels.Implement], ct);
        await gitBucket.AddCommentAsync(repoName, issue.Number,
            $"Copilot opened PR #{pull.Number} from `{branch}` into `{baseBranch}`." +
            (failing ? " Build, test or lint is still failing; the PR is marked WIP." : ""), ct);
        return new JobOutcome(failing ? "wip" : "ok",
            $"PR #{pull.Number}, {stats.Count} files, {lines} lines, {rounds.Count} copilot calls");
    }

    /// <summary>The tools of the configured commands with any arguments, plus allowShell; never git (see ShellPermissions).</summary>
    private static List<string> AllowTools(CommandsConfig commands) =>
        ["write", .. commands.AllowedForAgent().SelectMany(ShellPermissions.For).Distinct(),
         .. commands.AllowShell.Select(p => $"shell({p})")];

    /// <summary>Deny wins over allow in Copilot CLI, so git stays blocked whatever allowShell says.</summary>
    private static readonly string[] DenyTools = ["shell(git)", "shell(git:*)", "write(.ai-workflow)"];

    private static string Status(ProcessResult r) => r.Ok ? "passed" : r.TimedOut ? "timed out" : $"failed (exit {r.ExitCode})";

    private static string ChecksSummary(List<(string Command, ProcessResult Result)> checks) =>
        checks.Count == 0 ? "no checks configured" : string.Join(", ", checks.Select(c => $"`{c.Command}` {Status(c.Result)}"));

    /// <summary>
    /// Commits everything the agent wrote and pushes it to the branch (never forced), so a failed or
    /// oversized run cannot lose the work. Returns what was saved, or null when nothing changed.
    /// </summary>
    private async Task<IReadOnlyList<DiffStat>?> SaveWorkAsync(string dir, GbIssue issue, string branch, JobRun run, CancellationToken ct)
    {
        var stats = await git.StageAllAsync(dir, ct);
        run.Save("numstat.txt", string.Join("\n", stats.Select(s => $"{s.Added}\t{s.Deleted}\t{s.Path}")));
        if (stats.Count == 0) return null;

        await git.CommitAsync(dir, $"#{issue.Number}: {issue.Title} (Copilot)", ct);
        await git.PushAsync(dir, "HEAD", branch, ct);
        run.Info($"pushed {branch}: {stats.Count} files, {stats.Sum(s => s.Lines)} lines");
        return stats;
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

    private static string PullBody(int number, string prBody, List<(string Command, ProcessResult Result)> checks, bool failing,
        IReadOnlyList<string> rounds, IReadOnlyList<string> prBodyProblems)
    {
        var sb = new StringBuilder();
        if (failing)
            sb.AppendLine("**WIP: build, test or lint is still failing after Copilot stopped.** See the check output below.").AppendLine();
        sb.AppendLine(prBody.Trim()).AppendLine();
        sb.AppendLine("### Checks run by ai-runner");
        if (checks.Count == 0) sb.AppendLine("None configured (repos.yml `commands`).");
        foreach (var (command, result) in checks)
            sb.AppendLine($"- `{command}`: {Status(result)}");
        var failed = checks.FirstOrDefault(c => !c.Result.Ok);
        if (failed.Result is not null)
            sb.AppendLine().AppendLine("```").AppendLine(failed.Result.Tail(2000)).AppendLine("```");
        sb.AppendLine().AppendLine("### Rounds by ai-runner");
        foreach (var r in rounds) sb.AppendLine($"- {r}");
        if (prBodyProblems.Count > 0)
        {
            sb.AppendLine().AppendLine("### PR description problems (left after the last round)");
            foreach (var p in prBodyProblems) sb.AppendLine($"- {p}");
        }
        sb.AppendLine().AppendLine($"Closes #{number}");
        return sb.ToString();
    }
}
