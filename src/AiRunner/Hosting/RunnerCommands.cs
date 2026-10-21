using System.Threading.Channels;

namespace AiRunner.Hosting;

/// <summary>
/// The runtime controls of the dashboard (ADR 0004): poll now, pause and resume, cancel the running job. They act
/// inside the process and are not workflow state, so nothing here is stored: a restart resumes and forgets.
/// The runner and the dashboard web app share this one instance; it holds no token and no code-host client, so
/// giving it to the web app gives the web app nothing it could reach the code host with.
/// </summary>
public sealed class RunnerCommands
{
    /// <summary>The job whose run is going on, with the timeout source a cancel uses.</summary>
    private sealed record RunningJob(long Id, CancellationTokenSource Source)
    {
        public bool IsCancelled { get; set; }
    }

    private readonly Lock _lock = new();

    // Capacity 1 and DropWrite: any number of wakes before the poller reads one gives exactly one more poll.
    private readonly Channel<bool> _wakes = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private TaskCompletionSource? _resumed;
    private RunningJob? _running;

    /// <summary>The worker takes no new job; the health shows it.</summary>
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
    public Task WaitForWakeAsync(CancellationToken cancellationToken) =>
        _wakes.Reader.ReadAsync(cancellationToken).AsTask();

    /// <summary>The worker takes no new job until <see cref="Resume"/>; the running job and the poller go on.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? resumed;
        lock (_lock)
        {
            resumed = _resumed;
            _resumed = null;
        }

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
    public void RunStarted(long jobId, CancellationTokenSource source)
    {
        lock (_lock)
        {
            _running = new RunningJob(jobId, source);
        }
    }

    /// <summary>
    /// The job's own run is over (before its failure report and claim release): a cancel from now on is refused.
    /// Returns whether the dashboard cancelled it, so its failure says so instead of "did not finish in time".
    /// </summary>
    public bool RunEnded(long jobId)
    {
        lock (_lock)
        {
            if (_running is null || _running.Id != jobId)
            {
                return false;
            }

            var isCancelled = _running.IsCancelled;
            _running = null;

            return isCancelled;
        }
    }

    /// <summary>
    /// Cancels the running job through its timeout source, so the worker's timeout path reports it. False when that
    /// job is not running: a pending job cannot be cancelled (the next poll would only queue it again).
    /// </summary>
    public bool TryCancel(long jobId)
    {
        CancellationTokenSource source;
        lock (_lock)
        {
            if (_running is null || _running.Id != jobId)
            {
                return false;
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

        return true;
    }
}
