namespace AiRunner.Hosting;

/// <summary>The latest poll error of one project.</summary>
public sealed record ProjectPollError(string Project, string Message, DateTimeOffset At);

/// <summary>The job the worker runs now. <c>Round</c> is set by agent sessions (implement, review fix), else null.</summary>
public sealed record RunningJob(long Id, string Type, string Repo, int Number, DateTimeOffset StartedAt)
{
    public int? Round { get; init; }

    /// <summary>
    /// This runner's claim on the issue or PR went through (claim_enabled). A routine job claims only before its
    /// first write, so it can run without holding one.
    /// </summary>
    public bool HoldsClaim { get; init; }
}

/// <summary>A copy of <see cref="RunnerStatus"/> at one moment; safe to read on any thread.</summary>
public sealed record RunnerStatusSnapshot(
    DateTimeOffset StartedAt,
    string RunnerName,
    string ConfigPath,
    string CopilotVersion,
    DateTimeOffset? LastPollStartedAt,
    DateTimeOffset? LastPollEndedAt,
    DateTimeOffset? WorkerBusySince,
    IReadOnlyList<ProjectPollError> Errors,
    RunningJob? Running = null)
{
    /// <summary>The worker is paused from the dashboard; set from <see cref="RunnerCommands"/>, which owns it.</summary>
    public bool IsPaused { get; init; }

    /// <summary>No code-host token yet (setup mode); set from <see cref="IRunnerSetup"/>, which owns it.</summary>
    public bool IsSetupNeeded { get; init; }

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
    private RunningJob? _running;

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

    public void WorkerBusy(RunningJob? job = null)
    {
        lock (_lock)
        {
            _workerBusySince = time.GetUtcNow();
            _running = job;
        }
    }

    public void WorkerIdle()
    {
        lock (_lock)
        {
            _workerBusySince = null;
            _running = null;
        }
    }

    /// <summary>The running agent session starts round <paramref name="round"/>.</summary>
    public void JobRound(int round)
    {
        lock (_lock)
        {
            if (_running is not null)
            {
                _running = _running with { Round = round };
            }
        }
    }

    /// <summary>The running job now holds its claim: the dashboard names this runner as the holder.</summary>
    public void ClaimHeld()
    {
        lock (_lock)
        {
            if (_running is not null)
            {
                _running = _running with { HoldsClaim = true };
            }
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
                errors,
                _running);
        }
    }
}
