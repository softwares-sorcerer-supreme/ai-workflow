using System.Diagnostics;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Jobs;

namespace AiRunner.Hosting;

/// <summary>
/// Runs one job at a time, so cost is predictable. Every job gets the same treatment, driven
/// by its JobInfo: a log folder, a timeout, a cost row, and on failure a short comment (plus
/// ai:failed on issues). A job that found nothing to do leaves no trace.
/// </summary>
public sealed class JobWorker(
    RunnerConfig config,
    JobQueue queue,
    JobFactory factory,
    JobLog jobLog,
    ICostLog cost,
    IStateStore state,
    IGitBucketClient gitBucket,
    ILogger<JobWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await RecoverAsync();
        while (!ct.IsCancellationRequested)
        {
            StoredJob job;
            try { job = await queue.DequeueAsync(ct); }
            catch (OperationCanceledException) { break; }
            try { await RunOneAsync(job, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Never let one job stop the worker (and with it the whole host).
                log.LogError("{Request}: unexpected error in the worker: {Error}", job.Request, ex);
            }
        }
    }

    /// <summary>For --once: run what is queued now, then return.</summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        await RecoverAsync();
        while (queue.TryDequeue(out var job))
            await RunOneAsync(job!, ct);
    }

    private async Task RunOneAsync(StoredJob stored, CancellationToken ct)
    {
        var request = stored.Request;
        var job = factory.Get(request.Type);
        state.SetJobStatus(stored.Id, JobStatus.Running);
        var run = jobLog.Begin(request);
        var sw = Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(job.Info.Timeout);
        try
        {
            var outcome = await job.RunAsync(request, run, cts.Token);
            if (outcome.Quiet)
            {
                // e.g. sequencing checks every poll: nothing worth keeping.
                log.LogDebug("{Request}: {Result} ({Detail})", request, outcome.Result, outcome.Detail);
                state.DeleteJob(stored.Id);
                FileSystem.DeleteDirectory(run.Dir);
                return;
            }
            state.SetJobStatus(stored.Id, JobStatus.Done);
            run.Info($"{request}: {outcome.Result} {outcome.Detail} in {sw.Elapsed.TotalSeconds:F0}s");
            Record(request, job.Info, sw.Elapsed, outcome.Result, outcome.Detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The runner is stopping. The job stays "running" and is re-queued once on the next start.
            log.LogWarning("{Request} interrupted by shutdown", request);
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                JobFailedException => ex.Message,
                OperationCanceledException when cts.IsCancellationRequested =>
                    $"The job did not finish within {job.Info.Timeout.TotalMinutes:F0} minutes.",
                OperationCanceledException => "A call to GitBucket timed out (VPN or server down?).",
                _ => $"{ex.GetType().Name}: {Text.Short(ex.Message)}",
            };
            log.LogError("{Request} failed: {Message}", request, message);
            try
            {
                run.Save("error.txt", ex.ToString());
                state.SetJobStatus(stored.Id, JobStatus.Failed, message);
            }
            catch (Exception inner) { log.LogError("Could not store the failure of {Request}: {Message}", request, inner.Message); }
            Record(request, job.Info, sw.Elapsed, "failed", message);
            await ReportFailureAsync(job, request, message, Path.GetFileName(run.Dir));
        }
    }

    private async Task RecoverAsync()
    {
        state.PruneJobs(DateTimeOffset.UtcNow.AddDays(-config.LogRetentionDays));
        foreach (var stored in queue.Recover())
            await ReportFailureAsync(factory.Get(stored.Request.Type), stored.Request,
                "The runner was stopped twice while this job was running.", null);
    }

    /// <summary>Short comment on the issue or PR. Issues also get ai:failed, so nothing retries until a human looks.</summary>
    private async Task ReportFailureAsync(IJob job, JobRequest request, string message, string? logFolder)
    {
        var where = logFolder is null ? "" : $" (`{Path.Combine(config.Paths.Logs, "jobs", logFolder)}`)";
        var text = $"{job.Info.Description} failed: {message}\n\n" +
                   $"The full log is in the runner's log folder{where}. " +
                   (job.Info.OnPullRequest
                       ? "I will try again on the next commit."
                       : $"To retry, fix the cause and remove the `{Labels.Failed}` label.");
        try
        {
            job.OnFailed(request);
            await gitBucket.AddCommentAsync(request.Repo, request.Number, text, CancellationToken.None);
            if (!job.Info.OnPullRequest)
                await gitBucket.AddLabelsAsync(request.Repo, request.Number, [Labels.Failed], CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogError("Could not report the failure of {Request} to GitBucket: {Message}", request, ex.Message);
        }
    }

    private void Record(JobRequest request, JobInfo info, TimeSpan elapsed, string result, string detail) =>
        cost.Record(new CostEntry(DateTimeOffset.UtcNow, request.Repo, request.Number, info.Workflow,
            elapsed.TotalSeconds, result, detail));
}
