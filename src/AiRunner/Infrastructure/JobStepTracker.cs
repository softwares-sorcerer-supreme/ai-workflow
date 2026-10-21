using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>One step of a job as stored: open while <c>EndedAt</c> is null.</summary>
public sealed record JobStepRecord(
    long JobId,
    int Seq,
    string Code,
    string Label,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? Outcome,
    string? Detail);

/// <summary>How a step ended: the outcome (<c>JobSteps.Ok</c> or <c>Failed</c>) and why, when it failed.</summary>
public sealed record JobStepEnd(long JobId, int Seq, DateTimeOffset EndedAt, string Outcome, string? Detail);

/// <summary>The write side of the step log (the <c>job_step</c> table).</summary>
public interface IJobStepLog
{
    /// <summary>Removes the steps of a job, so a job that runs again starts a clean list.</summary>
    void ClearSteps(long jobId);

    /// <summary>Stores a step that has just started (its end fields are empty).</summary>
    void OpenStep(JobStepRecord step);

    void CloseStep(JobStepEnd end);
}

/// <summary>
/// Writes the steps of one job run. Steps are a log like the jobs table, so a store error is a warning and never
/// fails the job. A new step closes the one before it as ok; the job's end closes the last.
/// </summary>
public sealed class JobStepTracker
{
    private const int MaxDetailLength = 300;

    private readonly Lock _lock = new();
    private readonly long _jobId;
    private readonly IJobStepLog _log;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private int _seq;
    private bool _isOpen;

    public JobStepTracker(long jobId, IJobStepLog log, TimeProvider time, ILogger logger)
    {
        _jobId = jobId;
        _log = log;
        _time = time;
        _logger = logger;
        Attempt(() => _log.ClearSteps(jobId));
    }

    /// <summary>Told about every opened step (code, label), so the dashboard can name the current one.</summary>
    public Action<string, string>? Opened { get; init; }

    public void Open(string code, string label)
    {
        lock (_lock)
        {
            CloseOpenStep(JobSteps.Ok, null);

            _seq++;
            _isOpen = true;
            var startedAt = _time.GetUtcNow();
            var step = new JobStepRecord(_jobId, _seq, code, label, startedAt, null, null, null);
            Attempt(() => _log.OpenStep(step));
            Attempt(() => Opened?.Invoke(code, label));
        }
    }

    /// <summary>Closes the open step with this outcome; nothing happens when none is open.</summary>
    public void End(string outcome, string? detail)
    {
        lock (_lock)
        {
            CloseOpenStep(outcome, detail);
        }
    }

    private void CloseOpenStep(string outcome, string? detail)
    {
        if (!_isOpen)
        {
            return;
        }

        _isOpen = false;
        var endedAt = _time.GetUtcNow();
        var shortDetail = detail is null ? null : Text.Short(detail, MaxDetailLength);
        var end = new JobStepEnd(_jobId, _seq, endedAt, outcome, shortDetail);
        Attempt(() => _log.CloseStep(end));
    }

    private void Attempt(Action write)
    {
        try
        {
            write();
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Could not write a step of job {JobId}: {Message}", _jobId, exception.Message);
        }
    }
}
