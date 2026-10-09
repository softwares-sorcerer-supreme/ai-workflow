using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

/// <summary>One "copilot -p" call. Tool names follow "copilot help permissions" (write, shell(cmd), url(...)).</summary>
public sealed record CopilotRequest(string WorkDir, string Prompt, string? Agent, string? Model, TimeSpan Timeout)
{
    public IReadOnlyList<string> AllowTools { get; init; } = [];
    public IReadOnlyList<string> DenyTools { get; init; } = [];
    /// <summary>When set, the whole session (tool calls, denied commands, subagents) is exported there as Markdown; "-s" stdout only has the final answer.</summary>
    public string? TranscriptFile { get; init; }
    /// <summary>Images and PDFs attached to the prompt (copilot --attachment). Only set when the CLI supports the flag.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];
}

public interface ICopilotRunner
{
    Task<ProcessResult> RunAsync(CopilotRequest request, CancellationToken cancellationToken);

    /// <summary>True when the installed Copilot CLI has the --attachment flag (older versions do not).</summary>
    Task<bool> CanAttachFilesAsync(string workDir, CancellationToken cancellationToken);
}

/// <summary>
/// Runs "copilot -p" as a child process. The child gets NO tokens (ProcessRunner removes
/// them from every child), so Copilot uses the developer's /login and the agent cannot
/// call GitBucket or GitHub by itself.
/// </summary>
public sealed class CopilotRunner(
    RunnerConfig config,
    IProcessRunner processRunner,
    ILogger<CopilotRunner> logger) : ICopilotRunner
{
    private bool? _canAttachFiles;

    /// <summary>"copilot --help" is free (no premium request) and is asked once per run of the runner.</summary>
    public async Task<bool> CanAttachFilesAsync(string workDir, CancellationToken cancellationToken)
    {
        if (_canAttachFiles is { } known)
        {
            return known;
        }

        var helpTimeout = TimeSpan.FromSeconds(30);
        var help = await processRunner.RunAsync(
            new ProcessSpec(config.Copilot.Command, ["--help"], workDir, helpTimeout),
            cancellationToken);
        var hasFlag = help.Ok && help.StdOut.Contains("--attachment", StringComparison.Ordinal);
        _canAttachFiles = hasFlag;

        return hasFlag;
    }

    public async Task<ProcessResult> RunAsync(CopilotRequest request, CancellationToken cancellationToken)
    {
        List<string> args = ["-p", request.Prompt, "-s"];
        if (request.Agent is not null)
        {
            args.AddRange(["--agent", request.Agent]);
        }

        if (request.Model is not null)
        {
            args.AddRange(["--model", request.Model]);
        }

        var allowToolArgs = request.AllowTools.Select(tool => $"--allow-tool={tool}");
        args.AddRange(allowToolArgs);
        var denyToolArgs = request.DenyTools.Select(tool => $"--deny-tool={tool}");
        args.AddRange(denyToolArgs);
        if (request.TranscriptFile is not null)
        {
            args.AddRange(["--share", request.TranscriptFile]);
        }

        foreach (var attachment in request.Attachments)
        {
            args.AddRange(["--attachment", attachment]);
        }

        args.AddRange(config.Copilot.ExtraArgs);

        logger.LogInformation(
            "copilot --agent {Agent} --model {Model} in {Dir}",
            request.Agent ?? "(none)",
            request.Model ?? "(default)",
            request.WorkDir);

        var result = await processRunner.RunAsync(
            new ProcessSpec(config.Copilot.Command, args, request.WorkDir, request.Timeout),
            cancellationToken);

        logger.LogInformation(
            "copilot finished: exit {Exit}, {Seconds:F1}s, timedOut={TimedOut}",
            result.ExitCode,
            result.Duration.TotalSeconds,
            result.TimedOut);

        return result;
    }

    /// <summary>
    /// True when the output looks like the seat ran out of premium requests. The words are a
    /// guess from the CLI messages; when in doubt the job still fails, only the message differs.
    /// </summary>
    public static bool LooksLikeQuotaError(ProcessResult result)
    {
        var text = (result.StdErr + "\n" + result.StdOut).ToLowerInvariant();
        return text.Contains("premium request") || text.Contains("quota") || text.Contains("rate limit");
    }
}
