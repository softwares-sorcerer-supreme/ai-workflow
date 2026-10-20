using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Hosting;

namespace AiRunner.Web;

/// <summary>The worker: <c>idle</c> or <c>busy</c> (the running job itself is shown from the overview).</summary>
public sealed record WorkerHealth(string State, string StateLabel, DateTimeOffset? Since);

/// <summary>The latest poll error of one project.</summary>
public sealed record PollErrorResponse(string Project, string Message, DateTimeOffset At);

/// <summary>
/// <c>GET /api/health</c>: is the runner working? Every state comes as a stable code (for styling) plus the text to
/// show, so the UI never decides what "healthy" means. Times are UTC.
/// </summary>
public sealed record HealthResponse(
    string State,
    string StateLabel,
    string RunnerName,
    DateTimeOffset StartedAt,
    string ConfigPath,
    string CopilotVersion,
    int PollIntervalSeconds,
    DateTimeOffset? LastPollStartedAt,
    DateTimeOffset? LastPollEndedAt,
    bool IsPolling,
    WorkerHealth Worker,
    string ErrorsLabel,
    IReadOnlyList<PollErrorResponse> Errors)
{
    private const string WorkerIdle = "idle";
    private const string WorkerBusy = "busy";

    public static HealthResponse From(RunnerStatusSnapshot status, RunnerConfig config, DateTimeOffset now)
    {
        var pollInterval = TimeSpan.FromSeconds(config.PollIntervalSeconds);
        var input = new HealthInput(
            now,
            status.StartedAt,
            status.LastPollEndedAt,
            pollInterval,
            status.Errors.Count,
            PollRunningSince: status.IsPolling ? status.LastPollStartedAt : null);
        var health = RunnerHealth.Assess(input);

        var worker = status.WorkerBusySince is { } busySince
            ? new WorkerHealth(WorkerBusy, "Running a job", busySince)
            : new WorkerHealth(WorkerIdle, "Idle", null);

        var errors = status.Errors
            .Select(error => new PollErrorResponse(error.Project, error.Message, error.At))
            .ToList();

        return new HealthResponse(
            health.Code,
            health.Label,
            status.RunnerName,
            status.StartedAt,
            status.ConfigPath,
            status.CopilotVersion,
            config.PollIntervalSeconds,
            status.LastPollStartedAt,
            status.LastPollEndedAt,
            status.IsPolling,
            worker,
            ErrorsLabelOf(errors.Count),
            errors);
    }

    /// <summary>"1 poll error", "2 poll errors", or "No poll errors": the UI shows it as it is.</summary>
    private static string ErrorsLabelOf(int count) => count switch
    {
        0 => "No poll errors",
        1 => "1 poll error",
        _ => $"{count} poll errors",
    };
}
