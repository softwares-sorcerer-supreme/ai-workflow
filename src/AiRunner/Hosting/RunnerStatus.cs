namespace AiRunner.Hosting;

/// <summary>The latest poll error of one project.</summary>
public sealed record ProjectPollError(string Project, string Message, DateTimeOffset At);

/// <summary>A copy of <see cref="RunnerStatus"/> at one moment; safe to read on any thread.</summary>
public sealed record RunnerStatusSnapshot(
    DateTimeOffset StartedAt,
    string RunnerName,
    string ConfigPath,
    string CopilotVersion,
    DateTimeOffset? LastPollStartedAt,
    DateTimeOffset? LastPollEndedAt,
    DateTimeOffset? WorkerBusySince,
    IReadOnlyList<ProjectPollError> Errors)
{
    /// <summary>A poll has started and not finished yet.</summary>
    public bool IsPolling => LastPollStartedAt is { } started && (LastPollEndedAt is not { } ended || ended < started);
}

/// <summary>
/// What the runner is doing right now, in memory only, for the dashboard's health strip. Poller and JobWorker write
/// it; nothing reads it for a decision, so a restart that clears it changes nothing. Thread-safe: the poller, the
/// worker and web requests touch it at the same time.
/// </summary>
public sealed class RunnerStatus(string runnerName, string configPath, TimeProvider time)
{
    public const string UnknownVersion = "unknown";

    private readonly Lock _lock = new();
    private readonly DateTimeOffset _startedAt = time.GetUtcNow();
    private readonly Dictionary<string, ProjectPollError> _errors = new(StringComparer.Ordinal);
    private string _copilotVersion = UnknownVersion;
    private DateTimeOffset? _lastPollStartedAt;
    private DateTimeOffset? _lastPollEndedAt;
    private DateTimeOffset? _workerBusySince;

    public void SetCopilotVersion(string? version)
    {
        lock (_lock)
        {
            _copilotVersion = string.IsNullOrWhiteSpace(version) ? UnknownVersion : version.Trim();
        }
    }

    public void PollStarted()
    {
        lock (_lock)
        {
            _lastPollStartedAt = time.GetUtcNow();
        }
    }

    public void PollEnded()
    {
        lock (_lock)
        {
            _lastPollEndedAt = time.GetUtcNow();
        }
    }

    /// <summary>The project's poll went through: an older error of it no longer applies.</summary>
    public void ProjectPolled(string project)
    {
        lock (_lock)
        {
            _errors.Remove(project);
        }
    }

    public void ProjectFailed(string project, string message)
    {
        lock (_lock)
        {
            _errors[project] = new ProjectPollError(project, message, time.GetUtcNow());
        }
    }

    public void WorkerBusy()
    {
        lock (_lock)
        {
            _workerBusySince = time.GetUtcNow();
        }
    }

    public void WorkerIdle()
    {
        lock (_lock)
        {
            _workerBusySince = null;
        }
    }

    public RunnerStatusSnapshot Snapshot()
    {
        lock (_lock)
        {
            var errors = _errors.Values
                .OrderBy(error => error.Project, StringComparer.Ordinal)
                .ToList();

            return new RunnerStatusSnapshot(
                _startedAt,
                runnerName,
                configPath,
                _copilotVersion,
                _lastPollStartedAt,
                _lastPollEndedAt,
                _workerBusySince,
                errors);
        }
    }
}
