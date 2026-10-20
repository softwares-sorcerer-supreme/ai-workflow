using System.Diagnostics;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Jobs;

namespace AiRunner.Hosting;

/// <summary>
/// A job that ended, for the cost row: what ran, for how long and how it ended; its run folder holds the usage files
/// of what Copilot billed.
/// </summary>
public sealed record FinishedJob(long JobId, JobRequest Request, JobInfo Info, TimeSpan Elapsed, JobOutcome Outcome)
{
    public required JobRun Run { get; init; }
}

/// <summary>A job that failed and must be reported on its issue or PR.</summary>
/// <param name="LogFolder">Name of the job's log folder, or null when it has none.</param>
public sealed record JobFailure(IJob Job, JobRequest Request, string Message, string? LogFolder);

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
    ICostLog costLog,
    IStateStore stateStore,
    IGitBucketClient gitBucketClient,
    ClaimService claims,
    RunnerStatus status,
    ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await RecoverAsync();

        while (!cancellationToken.IsCancellationRequested)
        {
            StoredJob job;
            try
            {
                job = await queue.DequeueAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunOneAsync(job, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Never let one job stop the worker (and with it the whole host).
                logger.LogError("{Request}: unexpected error in the worker: {Error}", job.Request, exception);
            }
        }
    }

    /// <summary>For --once: run what is queued now, then return.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        await RecoverAsync();

        while (queue.TryDequeue(out var job))
        {
            await RunOneAsync(job!, cancellationToken);
        }
    }

    private async Task RunOneAsync(StoredJob stored, CancellationToken cancellationToken)
    {
        var request = stored.Request;
        var job = factory.Get(request.Type);

        stateStore.SetJobStatus(stored.Id, JobStatus.Running);
        var run = jobLog.Begin(request);
        var logFolder = Path.GetFileName(run.Dir);
        stateStore.SetJobLogDir(stored.Id, logFolder);
        run.RoundChanged = status.JobRound;
        var stopwatch = Stopwatch.StartNew();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = job.Info.Timeout;
        run.Deadline = DateTimeOffset.UtcNow + timeout;
        timeoutSource.CancelAfter(timeout);

        // Claims (claim_enabled): a normal job claims its issue or PR up front, a routine one just before its first
        // write (JobRun.ClaimAsync). Whatever was claimed is released when the job ends.
        var target = new ClaimTarget(request.Repo, request.Number) { Until = run.Deadline };
        var isClaimHeld = false;
        var runningJob = new RunningJob(
            stored.Id,
            request.Type.ToString(),
            request.Repo,
            request.Number,
            DateTimeOffset.UtcNow);
        status.WorkerBusy(runningJob);

        // Both kinds of job claim through here, so the dashboard learns of every claim that went through.
        async Task<bool> TryClaimAsync(CancellationToken token)
        {
            var isClaimed = await claims.TryClaimAsync(target, token);
            if (isClaimed)
            {
                status.ClaimHeld();
            }

            return isClaimed;
        }

        try
        {
            if (claims.IsEnabled(request.Repo))
            {
                if (job.Info.Routine)
                {
                    run.Claimer = async token =>
                    {
                        isClaimHeld = isClaimHeld || await TryClaimAsync(token);

                        return isClaimHeld;
                    };
                }
                else
                {
                    isClaimHeld = await TryClaimAsync(timeoutSource.Token);
                    if (!isClaimHeld)
                    {
                        logger.LogDebug("{Request}: claimed by another runner", request);
                        stateStore.DeleteJob(stored.Id);
                        FileSystem.DeleteDirectory(run.Dir);
                        return;
                    }
                }
            }

            var outcome = await job.RunAsync(request, run, timeoutSource.Token);
            if (outcome.Quiet)
            {
                // e.g. sequencing checks every poll: nothing worth keeping (Record keeps a cost row only if Copilot
                // billed it).
                logger.LogDebug("{Request}: {Result} ({Detail})", request, outcome.Result, outcome.Detail);
                var quiet = new FinishedJob(stored.Id, request, job.Info, stopwatch.Elapsed, outcome) { Run = run };
                Record(quiet);
                stateStore.DeleteJob(stored.Id);
                FileSystem.DeleteDirectory(run.Dir);
                return;
            }

            stateStore.SetJobStatus(stored.Id, JobStatus.Done);
            run.Info($"{request}: {outcome.Result} {outcome.Detail} in {stopwatch.Elapsed.TotalSeconds:F0}s");
            var finished = new FinishedJob(stored.Id, request, job.Info, stopwatch.Elapsed, outcome) { Run = run };
            Record(finished);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The runner is stopping. The job stays "running" and is re-queued once on the next start.
            logger.LogWarning("{Request} interrupted by shutdown", request);
        }
        catch (Exception exception)
        {
            var message = exception switch
            {
                JobFailedException => exception.Message,
                OperationCanceledException when timeoutSource.IsCancellationRequested =>
                    $"The job did not finish within {job.Info.Timeout.TotalMinutes:F0} minutes.",
                OperationCanceledException => "A call to GitBucket timed out (VPN or server down?).",
                _ => $"{exception.GetType().Name}: {Text.Short(exception.Message)}",
            };

            logger.LogError("{Request} failed: {Message}", request, message);

            try
            {
                var errorText = exception.ToString();
                run.Save("error.txt", errorText);
                stateStore.SetJobStatus(stored.Id, JobStatus.Failed, message);
            }
            catch (Exception innerException)
            {
                logger.LogError(
                    "Could not store the failure of {Request}: {Message}",
                    request,
                    innerException.Message);
            }

            var failedOutcome = new JobOutcome("failed", message);
            var failedJob = new FinishedJob(stored.Id, request, job.Info, stopwatch.Elapsed, failedOutcome) { Run = run };
            Record(failedJob);

            var failure = new JobFailure(job, request, message, logFolder);
            await ReportFailureAsync(failure);
        }
        finally
        {
            // On shutdown the claim stays: this runner takes it back at once on the next start (same name).
            if (isClaimHeld && !cancellationToken.IsCancellationRequested)
            {
                await ReleaseClaimAsync(target);
            }

            status.WorkerIdle();
        }
    }

    private async Task ReleaseClaimAsync(ClaimTarget target)
    {
        try
        {
            await claims.ReleaseAsync(target, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The claim then goes stale after 30 minutes and any runner can take it over.
            logger.LogError("Could not release the claim on {Repo}#{Number}: {Message}", target.Repo, target.Number, exception.Message);
        }
    }

    private async Task RecoverAsync()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-config.LogRetentionDays);
        stateStore.PruneJobs(cutoff);

        foreach (var stored in queue.Recover())
        {
            var job = factory.Get(stored.Request.Type);
            var failure = new JobFailure(
                job,
                stored.Request,
                "The runner was stopped twice while this job was running.",
                LogFolder: null);
            await ReportFailureAsync(failure);
        }
    }

    /// <summary>Short comment on the issue or PR. Issues also get ai:failed, so nothing retries until a human looks.</summary>
    private async Task ReportFailureAsync(JobFailure failure)
    {
        var (job, request, message, logFolder) = failure;
        var where = logFolder is null ? "" : $" (`{Path.Combine(config.Paths.Logs, "jobs", logFolder)}`)";
        var keyword = config.TriggerKeywordOf(request.Repo);
        var retry = job.Info.Retry?.Replace(JobInfo.KeywordPlaceholder, keyword);
        var text = $"{job.Info.Description} failed: {message}\n\n" +
                   $"The full log is in the runner's log folder{where}. " +
                   (retry ?? (job.Info.OnPullRequest
                       ? "I will try again on the next commit."
                       : $"To retry, fix the cause and remove the `{Labels.Failed}` label."));

        try
        {
            job.OnFailed(request);
            await gitBucketClient.AddCommentAsync(request.Repo, request.Number, text, CancellationToken.None);

            if (!job.Info.OnPullRequest)
            {
                await gitBucketClient.AddLabelsAsync(
                    request.Repo,
                    request.Number,
                    [Labels.Failed],
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Could not report the failure of {Request} to GitBucket: {Message}",
                request,
                exception.Message);
        }
    }

    /// <summary>
    /// The cost row and the job result. Copilot bills every run, so a failed job's usage files count like a good
    /// one's; a quiet job leaves a row only when it ran Copilot (none does today).
    /// </summary>
    private void Record(FinishedJob finished)
    {
        var (jobId, request, info, elapsed, outcome) = finished;
        var usage = finished.Run.ReadUsage();
        if (outcome.Quiet && usage is null)
        {
            return;
        }

        var entry = new CostEntry(
            DateTimeOffset.UtcNow,
            request.Repo,
            request.Number,
            info.Workflow,
            elapsed.TotalSeconds,
            outcome.Result,
            outcome.Detail,
            outcome.CopilotCalls,
            usage);
        costLog.Record(entry);

        var jobResult = new JobResult(outcome.Result, outcome.Detail, outcome.CopilotCalls);

        try
        {
            stateStore.SetJobResult(jobId, jobResult);
        }
        catch (Exception exception)
        {
            // The job already ended: a database error here must not turn a finished job into a failed one, or stop
            // the failure report of a job that failed. Like the cost row, the result is best effort.
            logger.LogWarning("Could not store the result of job {JobId}: {Message}", jobId, exception.Message);
        }
    }
}
