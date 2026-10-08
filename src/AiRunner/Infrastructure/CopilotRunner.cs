using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

/// <summary>One "copilot -p" call. Tool names follow "copilot help permissions" (write, shell(cmd), url(...)).</summary>
public sealed record CopilotRequest(string WorkDir, string Prompt, string? Agent, string? Model, TimeSpan Timeout)
{
    public IReadOnlyList<string> AllowTools { get; init; } = [];
    public IReadOnlyList<string> DenyTools { get; init; } = [];
    /// <summary>When set, the whole session (tool calls, denied commands, subagents) is exported there as Markdown; "-s" stdout only has the final answer.</summary>
    public string? TranscriptFile { get; init; }
}

public interface ICopilotRunner
{
    Task<ProcessResult> RunAsync(CopilotRequest request, CancellationToken ct);
}

/// <summary>
/// Runs "copilot -p" as a child process. The child gets NO tokens (ProcessRunner removes
/// them from every child), so Copilot uses the developer's /login and the agent cannot
/// call GitBucket or GitHub by itself.
/// </summary>
public sealed class CopilotRunner(RunnerConfig config, IProcessRunner processes, ILogger<CopilotRunner> log) : ICopilotRunner
{
    public async Task<ProcessResult> RunAsync(CopilotRequest request, CancellationToken ct)
    {
        List<string> args = ["-p", request.Prompt, "-s"];
        if (request.Agent is not null) args.AddRange(["--agent", request.Agent]);
        if (request.Model is not null) args.AddRange(["--model", request.Model]);
        args.AddRange(request.AllowTools.Select(t => $"--allow-tool={t}"));
        args.AddRange(request.DenyTools.Select(t => $"--deny-tool={t}"));
        if (request.TranscriptFile is not null) args.AddRange(["--share", request.TranscriptFile]);
        args.AddRange(config.Copilot.ExtraArgs);

        log.LogInformation("copilot --agent {Agent} --model {Model} in {Dir}",
            request.Agent ?? "(none)", request.Model ?? "(default)", request.WorkDir);
        var result = await processes.RunAsync(
            new ProcessSpec(config.Copilot.Command, args, request.WorkDir, request.Timeout), ct);

        log.LogInformation("copilot finished: exit {Exit}, {Seconds:F1}s, timedOut={TimedOut}",
            result.ExitCode, result.Duration.TotalSeconds, result.TimedOut);
        return result;
    }

    /// <summary>
    /// True when the output looks like the seat ran out of premium requests. The words are a
    /// guess from the CLI messages; when in doubt the job still fails, only the message differs.
    /// </summary>
    public static bool LooksLikeQuotaError(ProcessResult r)
    {
        var text = (r.StdErr + "\n" + r.StdOut).ToLowerInvariant();
        return text.Contains("premium request") || text.Contains("quota") || text.Contains("rate limit");
    }
}
