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
    /// <summary>
    /// Where the CLI writes the tokens and credits of the run (--usage-output-file). Only used when the CLI has the flag.
    /// </summary>
    public string? UsageFile { get; init; }
}

public interface ICopilotRunner
{
    Task<ProcessResult> RunAsync(CopilotRequest request, CancellationToken cancellationToken);

    /// <summary>True when the installed Copilot CLI has the --attachment flag (older versions do not).</summary>
    Task<bool> CanAttachFilesAsync(string workDir, CancellationToken cancellationToken);

    /// <summary>The first line of "copilot --version", or null when the CLI cannot be started.</summary>
    Task<string?> GetVersionAsync(CancellationToken cancellationToken);
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
    private string? _helpText;

    public async Task<bool> CanAttachFilesAsync(string workDir, CancellationToken cancellationToken)
    {
        var help = await HelpTextAsync(workDir, cancellationToken);

        return help.Contains("--attachment", StringComparison.Ordinal);
    }

    /// <summary>
    /// "copilot --help" is free (no credits) and is asked once per run of the runner. A CLI that cannot answer gives
    /// an empty text, so no optional flag is used.
    /// </summary>
    private async Task<string> HelpTextAsync(string workDir, CancellationToken cancellationToken)
    {
        if (_helpText is { } known)
        {
            return known;
        }

        var helpTimeout = TimeSpan.FromSeconds(30);
        var help = await processRunner.RunAsync(
            new ProcessSpec(config.Copilot.Command, ["--help"], workDir, helpTimeout),
            cancellationToken);
        var helpText = help.Ok ? help.StdOut : "";
        _helpText = helpText;

        return helpText;
    }

    /// <summary>"copilot --version" is free (no premium request); the dashboard asks it once at start.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        var versionTimeout = TimeSpan.FromSeconds(30);
        var workDir = Directory.GetCurrentDirectory();
        var result = await processRunner.RunAsync(
            new ProcessSpec(config.Copilot.Command, ["--version"], workDir, versionTimeout),
            cancellationToken);

        if (!result.Ok)
        {
            return null;
        }

        var firstLine = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return firstLine;
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

        // An older CLI stops on a flag it does not know, so the usage file is asked for only when --help lists it.
        if (request.UsageFile is not null)
        {
            var help = await HelpTextAsync(request.WorkDir, cancellationToken);
            if (help.Contains("--usage-output-file", StringComparison.Ordinal))
            {
                args.AddRange(["--usage-output-file", request.UsageFile]);
            }
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
    /// True when the output looks like the seat ran out of premium requests (or AI credits). The words are a
    /// guess from the CLI messages; when in doubt the job still fails, only the message differs.
    /// </summary>
    public static bool LooksLikeQuotaError(ProcessResult result)
    {
        var text = (result.StdErr + "\n" + result.StdOut).ToLowerInvariant();
        return text.Contains("premium request") || text.Contains("quota") || text.Contains("rate limit");
    }
}
