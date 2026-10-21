using System.Threading.Channels;

namespace AiRunner.Hosting;

/// <summary>What <see cref="RunnerCommands.Cancel"/> did.</summary>
public enum CancelResult
{
    Cancelled,
    NotRunning,
}

/// <summary>
/// The running job as <see cref="RunnerCommands"/> sees it. The worker disposes it when the job ends, before it
/// disposes the job's token source, so a later cancel finds no job.
/// </summary>
public sealed class RunningJobHandle : IDisposable
{
    private readonly Action<RunningJobHandle> _release;

    internal RunningJobHandle(long jobId, CancellationTokenSource source, Action<RunningJobHandle> release)
    {
        JobId = jobId;
        Source = source;
        _release = release;
    }

    public long JobId { get; }

    internal CancellationTokenSource Source { get; }

    /// <summary>The dashboard cancelled this job; its failure says so instead of "did not finish in time".</summary>
    public bool IsCancelled { get; internal set; }

    /// <summary>Safe to call more than once: the worker releases it when the run ends and again at the job's end.</summary>
    public void Dispose() => _release(this);
}

/// <summary>
/// The runtime controls of the dashboard (ADR 0004): poll now, pause and resume, cancel the running job. They act
/// inside the process and are not workflow state, so nothing here is stored: a restart resumes and forgets.
/// The runner and the dashboard web app share this one instance; it holds no token and no code-host client, so
/// giving it to the web app gives the web app nothing it could reach the code host with.
/// </summary>
public sealed class RunnerCommands(RunnerStatus status)
{
    private readonly Lock _lock = new();

    // Capacity 1 and DropWrite: any number of wakes before the poller reads one gives exactly one more poll.
    private readonly Channel<bool> _wakes = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private TaskCompletionSource? _resumed;
    private RunningJobHandle? _running;

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _resumed is not null;
            }
        }
    }

    /// <summary>Asks the poller for a poll now; a poll that is running finishes first.</summary>
    public void WakePoller() => _wakes.Writer.TryWrite(true);

    /// <summary>Completes on the next <see cref="WakePoller"/> (or one that came before and was not read yet).</summary>
    public async Task WaitForWakeAsync(CancellationToken cancellationToken) =>
        await _wakes.Reader.ReadAsync(cancellationToken);

    /// <summary>The worker takes no new job until <see cref="Resume"/>; the running job and the poller go on.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        status.SetPaused(true);
    }

    public void Resume()
    {
        TaskCompletionSource? resumed;
        lock (_lock)
        {
            resumed = _resumed;
            _resumed = null;
        }

        // The health first, so it never shows "paused" while a job already starts.
        status.SetPaused(false);
        resumed?.SetResult();
    }

    /// <summary>Returns at once when not paused, else when resumed.</summary>
    public async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        Task? resumed;
        lock (_lock)
        {
            resumed = _resumed?.Task;
        }

        if (resumed is not null)
        {
            await resumed.WaitAsync(cancellationToken);
        }
    }

    /// <summary>The worker starts a job; <paramref name="source"/> is the job's timeout source, which cancel uses too.</summary>
    public RunningJobHandle Track(long jobId, CancellationTokenSource source)
    {
        var handle = new RunningJobHandle(jobId, source, Release);
        lock (_lock)
        {
            _running = handle;
        }

        return handle;
    }

    /// <summary>
    /// Cancels the running job through its timeout source, so the worker's timeout path reports it. A pending job
    /// cannot be cancelled: the next poll would only queue it again.
    /// </summary>
    public CancelResult Cancel(long jobId)
    {
        CancellationTokenSource source;
        lock (_lock)
        {
            if (_running is null || _running.JobId != jobId)
            {
                return CancelResult.NotRunning;
            }

            _running.IsCancelled = true;
            source = _running.Source;
        }

        // Outside the lock: cancelling runs the token's callbacks (the Copilot process is killed) on this thread.
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job ended between the lookup and the cancel: there is nothing left to stop.
        }

        return CancelResult.Cancelled;
    }

    private void Release(RunningJobHandle handle)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_running, handle))
            {
                _running = null;
            }
        }
    }
}
