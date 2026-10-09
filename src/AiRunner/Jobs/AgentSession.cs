using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>What a writing agent (implement, review fix) is asked to do and how its output file is checked.</summary>
/// <param name="Workflow">repos.yml workflow name, for the model.</param>
/// <param name="FirstRound">Name of round 1 in the round lines, e.g. "implement".</param>
/// <param name="OutputFile">The file the agent writes for the runner (PR_BODY.md).</param>
/// <param name="Problems">Checks the output file (null = missing); empty when it is complete.</param>
/// <param name="DiffBase">diff.patch of a fix round shows the changes since this ref.</param>
public sealed record AgentTask(
    string Agent,
    string Workflow,
    string FirstRound,
    string Prompt,
    string FixPrompt,
    string OutputFile,
    Func<string?, IReadOnlyList<string>> Problems,
    string DiffBase)
{
    /// <summary>Files for the --attachment flag; only round 1 gets them, later rounds can open them from disk.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];
}

/// <summary>Where an agent runs: the repo it works on, its clone, and the log of the job.</summary>
public sealed record AgentRunContext(RepoConfig Repo, string Directory, JobRun Run);

/// <summary>Work in the clone that gets committed and pushed to a branch.</summary>
public sealed record WorkToSave(string Directory, string Message, string Branch);

/// <summary>A command the runner ran after the agent stopped.</summary>
public sealed record CheckRun(string Command, ProcessResult Result);

/// <param name="FirstFailure">Copilot failed in round 1; the job decides what to save. Null otherwise.</param>
/// <param name="OutputText">The output file after the last round, or null.</param>
/// <param name="Rounds">One line per Copilot call, for the PR body or the reply.</param>
public sealed record AgentResult(
    JobFailedException? FirstFailure,
    IReadOnlyList<CheckRun> Checks,
    string? OutputText,
    IReadOnlyList<string> OutputProblems,
    IReadOnlyList<string> Rounds)
{
    public bool Failing => Checks.Any(check => !check.Result.Ok);
}

/// <summary>
/// The part Workflow 2 and the review fix share: setup, then the agent goes through its stages
/// (verify, review and simplify, output file) and the runner checks the result on its own. Build,
/// test, lint or output file problems go back for up to implement.maxFixRounds fix rounds. The
/// agent runs code in the clone, so .git/config is guarded after every step. Committing and
/// pushing stay with the job.
/// </summary>
public sealed class AgentSession(
    RunnerConfig config,
    IWorkspace workspace,
    IGitClient git,
    ICopilotRunner copilot,
    IProcessRunner processRunner)
{
    /// <summary>Setup, then per round Copilot and the checks (each up to the same timeout); the repo with the most fix rounds counts.</summary>
    public static TimeSpan JobTimeout(RunnerConfig config)
    {
        var mostFixRounds = config.Repos
            .Select(repoConfig => Math.Max(0, repoConfig.Implement.MaxFixRounds))
            .DefaultIfEmpty(0)
            .Max();

        return TimeSpan.FromMinutes(config.Copilot.ImplementTimeoutMinutes * (2 + 2 * mostFixRounds) + 10);
    }

    public async Task<AgentResult> RunAsync(
        AgentTask task,
        AgentRunContext context,
        CancellationToken cancellationToken)
    {
        var (repo, directory, run) = context;
        var timeout = TimeSpan.FromMinutes(config.Copilot.ImplementTimeoutMinutes);

        async Task<ProcessResult> RunCommandAsync(string command, string logName)
        {
            var processSpec = ProcessSpec.Shell(command, directory, timeout);
            var result = await processRunner.RunAsync(processSpec, cancellationToken);
            var report = result.Report(command);
            run.Save(logName, report);
            run.Info($"'{command}': exit {result.ExitCode}");
            return result;
        }

        // Dependencies first: the work folder was cleaned, so node_modules, .venv etc. are gone.
        if (!string.IsNullOrWhiteSpace(repo.Commands.Setup))
        {
            var setupCommand = repo.Commands.Setup.Trim();
            var setup = await RunCommandAsync(setupCommand, "setup.txt");
            if (!setup.Ok)
            {
                throw new JobFailedException(
                    $"The setup command `{repo.Commands.Setup}` {Status(setup)}: {setup.Tail(600)}");
            }
        }

        var gitConfig = git.ConfigFingerprint(directory);

        // The agent and the checks run code in this clone. If .git/config changed, git itself could be
        // made to run that code or send the token elsewhere: run no more git here, throw the clone away.
        void GuardGitConfig()
        {
            if (git.ConfigFingerprint(directory) == gitConfig)
            {
                return;
            }

            workspace.Discard(repo.FullName);
            throw new JobFailedException("Something in the work folder changed .git/config during the run. " +
                                         "I deleted the clone and pushed nothing. Please check the agent output in the log.");
        }

        // Returns the failure instead of throwing: round 1 is the job's call, a failed fix round still ends well.
        var model = repo.ModelFor(task.Workflow);
        var allowTools = AllowTools(repo.Commands);

        async Task<JobFailedException?> RunAgentAsync(string prompt, int round)
        {
            JobFailedException? failure = null;
            try
            {
                var transcriptFile = Path.Combine(run.Dir, $"round-{round}-transcript.md");
                await copilot.RunCopilotAsync(
                    new CopilotRequest(directory, prompt, task.Agent, model, timeout)
                    {
                        AllowTools = allowTools,
                        DenyTools = DenyTools,
                        TranscriptFile = transcriptFile,
                        Attachments = round == 1 ? task.Attachments : [],
                    }, run, cancellationToken, $"round-{round}-copilot");
            }
            catch (JobFailedException exception)
            {
                failure = exception;
            }

            GuardGitConfig();
            return failure;
        }

        // Build, then test and lint. After a failed build the rest would only repeat its errors; test
        // and lint both run, so one fix round gets every failure at once.
        async Task<List<CheckRun>> RunChecksAsync(int round)
        {
            var checks = new List<CheckRun>();
            var buildFirst = !string.IsNullOrWhiteSpace(repo.Commands.Build);   // Verification() starts with Build
            foreach (var command in repo.Commands.Verification())
            {
                var result = await RunCommandAsync(command, $"round-{round}-check-{checks.Count + 1}.txt");
                checks.Add(new CheckRun(command, result));
                if (!result.Ok && buildFirst && checks.Count == 1)
                {
                    break;
                }
            }

            GuardGitConfig();
            return checks;
        }

        var outputFile = Path.Combine(directory, task.OutputFile);

        // The agent verifies, reviews and writes its output file itself. The runner checks the result on its
        // own (the agent's claim is not enough) and, while fix rounds are left, sends back what is wrong.
        if (await RunAgentAsync(task.Prompt, 1) is { } firstFailure)
        {
            return new AgentResult(firstFailure, [], null, [], []);
        }

        var rounds = new List<string>();
        string? fixFailure = null;
        string? diffBeforeFix = null;
        List<CheckRun> checks = [];
        string? outputText;
        IReadOnlyList<string> problems;
        for (var round = 1; ; round++)
        {
            // A fix round that changed no code (only the output file, or Copilot failed early)
            // keeps the checks it had.
            if (diffBeforeFix is null ||
                await git.StageAndDiffAsync(directory, task.DiffBase, cancellationToken) != diffBeforeFix)
            {
                checks = await RunChecksAsync(round);
            }
            else
            {
                run.Info($"round {round}: no code changed, the checks of round {round - 1} still hold");
            }

            outputText = File.Exists(outputFile) ? await File.ReadAllTextAsync(outputFile, cancellationToken) : null;
            problems = task.Problems(outputText);
            rounds.Add($"Round {round} ({(round == 1 ? task.FirstRound : "fix")}): " +
                       (fixFailure is null ? "" : $"Copilot failed ({fixFailure}); ") +
                       $"{ChecksSummary(checks)}; " +
                       $"{task.OutputFile} {(problems.Count == 0 ? "complete" : $"{problems.Count} problem(s)")}");

            var timeLeft = ImplementGate.TimeForAnotherRound(run.TimeLeft, timeout);
            var checksOk = checks.All(check => check.Result.Ok);
            var progress = new ImplementProgress(
                ChecksOk: checksOk,
                PrBodyProblems: problems.Count,
                FixRoundsUsed: round - 1,
                MaxFixRounds: repo.Implement.MaxFixRounds,
                CanRunAgain: fixFailure is null && timeLeft);
            var step = ImplementGate.Decide(progress);
            if (step != ImplementStep.Fix)
            {
                if (step == ImplementStep.OutOfRounds && !timeLeft)
                {
                    run.Info($"round {round}: too little job time left for another fix round");
                }

                break;
            }

            var next = round + 1;
            var failures = checks.Where(check => !check.Result.Ok)
                .Select(check => new CheckFailure(check.Command, Status(check.Result), check.Result.Tail(3000)))
                .ToList();
            var fixInput = new FixRoundInput(
                Round: next,
                Failures: failures,
                OutputFile: task.OutputFile,
                OutputProblems: problems);
            var fix = ContextBuilder.ForFix(fixInput);
            diffBeforeFix = await git.StageAndDiffAsync(directory, task.DiffBase, cancellationToken);
            var fixFile = Path.Combine(directory, "fix.md");
            await File.WriteAllTextAsync(fixFile, fix, cancellationToken);
            var diffFile = Path.Combine(directory, "diff.patch");
            await File.WriteAllTextAsync(diffFile, diffBeforeFix, cancellationToken);
            run.Save($"fix-{next}.md", fix);
            run.Info($"{rounds[^1]}; starting fix round {next}");

            if (await RunAgentAsync(task.FixPrompt, next) is { } failed)
            {
                // Often the quota or the timeout: no more rounds, but what exists is still saved.
                fixFailure = Text.Short(failed.Message, 200);
                run.Info($"fix round {next}: {fixFailure}");
            }
        }

        run.SaveCopy(outputFile, task.OutputFile);
        return new AgentResult(null, checks, outputText, problems, rounds);
    }

    /// <summary>
    /// Commits everything the agent wrote and pushes it to the branch (never forced), so the work is not lost.
    /// Returns what was saved, or null when nothing changed. A rejected push throws InvalidOperationException.
    /// </summary>
    public async Task<IReadOnlyList<DiffStat>?> SaveWorkAsync(
        WorkToSave work,
        JobRun run,
        CancellationToken cancellationToken)
    {
        var (directory, message, branch) = work;
        var stats = await git.StageAllAsync(directory, cancellationToken);
        var statLines = stats.Select(stat => $"{stat.Added}\t{stat.Deleted}\t{stat.Path}");
        var numstat = string.Join("\n", statLines);
        run.Save("numstat.txt", numstat);
        if (stats.Count == 0)
        {
            return null;
        }

        await git.CommitAsync(directory, message, cancellationToken);
        await git.PushAsync(directory, "HEAD", branch, cancellationToken);
        run.Info($"pushed {branch}: {stats.Count} files, {stats.Sum(stat => stat.Lines)} lines");

        return stats;
    }

    /// <summary>The tools of the configured commands with any arguments, plus allowShell; never git (see ShellPermissions).</summary>
    public static List<string> AllowTools(CommandsConfig commands) =>
    [
        "write",
        .. commands.AllowedForAgent().SelectMany(ShellPermissions.For).Distinct(),
        .. commands.AllowShell.Select(pattern => $"shell({pattern})"),
    ];

    /// <summary>Deny wins over allow in Copilot CLI, so git stays blocked whatever allowShell says.</summary>
    public static readonly string[] DenyTools = ["shell(git)", "shell(git:*)", "write(.ai-workflow)"];

    public static string Status(ProcessResult result) =>
        result.Ok ? "passed" : result.TimedOut ? "timed out" : $"failed (exit {result.ExitCode})";

    private static string ChecksSummary(List<CheckRun> checks)
    {
        if (checks.Count == 0)
        {
            return "no checks configured";
        }

        var summaries = checks.Select(check => $"`{check.Command}` {Status(check.Result)}");

        return string.Join(", ", summaries);
    }

    /// <summary>"Checks run by ai-runner", "Rounds by ai-runner" and the output file problems left (under problemsTitle), for a PR body or a reply.</summary>
    public static void AppendReport(StringBuilder builder, AgentResult result, string problemsTitle)
    {
        builder.AppendLine("### Checks run by ai-runner");
        if (result.Checks.Count == 0)
        {
            builder.AppendLine("None configured (repos.yml `commands`).");
        }

        foreach (var (command, commandResult) in result.Checks)
        {
            builder.AppendLine($"- `{command}`: {Status(commandResult)}");
        }

        var failed = result.Checks.FirstOrDefault(check => !check.Result.Ok);
        if (failed is not null)
        {
            var failedOutput = failed.Result.Tail(2000);
            builder.AppendLine().AppendLine("```").AppendLine(failedOutput).AppendLine("```");
        }

        builder.AppendLine().AppendLine("### Rounds by ai-runner");
        foreach (var roundLine in result.Rounds)
        {
            builder.AppendLine($"- {roundLine}");
        }

        if (result.OutputProblems.Count > 0)
        {
            builder.AppendLine().AppendLine($"### {problemsTitle} (left after the last round)");
            foreach (var problem in result.OutputProblems)
            {
                builder.AppendLine($"- {problem}");
            }
        }
    }
}
