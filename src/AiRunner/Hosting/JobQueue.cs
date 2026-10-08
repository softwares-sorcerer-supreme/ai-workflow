using System.Threading.Channels;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

/// <summary>
/// In-memory channel of job ids, backed by the jobs table so a restart loses nothing.
/// At most one pending job per key; a newer request only updates the payload (e.g. the
/// newest PR head). A job for the same key may be pending while another one runs: the
/// second run re-reads GitBucket and does nothing if the first one already did the work.
/// </summary>
public sealed class JobQueue(IStateStore state, ILogger<JobQueue> log)
{
    /// <summary>A job interrupted this many times (runner stopped, laptop slept) is given up.</summary>
    public const int MaxAttempts = 2;

    private readonly Channel<long> _ids = Channel.CreateUnbounded<long>();
    private readonly object _gate = new();

    /// <summary>True when a new job was added; false when the same work is already pending.</summary>
    public bool Enqueue(JobRequest request)
    {
        lock (_gate)
        {
            var pending = state.FindJob(request.Key, JobStatus.Pending);
            if (pending is not null)
            {
                if (pending.Request.Payload != request.Payload)
                    state.UpdateJobPayload(pending.Id, request.Payload);
                return false;
            }
            var id = state.AddJob(request);
            _ids.Writer.TryWrite(id);
            log.LogDebug("Queued job {Id}: {Request}", id, request);
            return true;
        }
    }

    public async Task<StoredJob> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            var job = Load(await _ids.Reader.ReadAsync(ct));
            if (job is not null) return job;
        }
    }

    public bool TryDequeue(out StoredJob? job)
    {
        while (_ids.Reader.TryRead(out var id))
        {
            job = Load(id);
            if (job is not null) return true;
        }
        job = null;
        return false;
    }

    /// <summary>
    /// Call once at startup. Jobs left "running" by a crash or stop are queued again once;
    /// returns the ones given up, so the caller can report them.
    /// </summary>
    public IReadOnlyList<StoredJob> Recover()
    {
        var givenUp = new List<StoredJob>();
        foreach (var job in state.ListJobs(JobStatus.Running))
        {
            if (job.Attempts >= MaxAttempts)
            {
                state.SetJobStatus(job.Id, JobStatus.Failed, "interrupted twice");
                givenUp.Add(job);
            }
            else
            {
                state.SetJobStatus(job.Id, JobStatus.Pending);
                log.LogInformation("Re-queued interrupted job {Request}", job.Request);
            }
        }
        foreach (var job in state.ListJobs(JobStatus.Pending))
            _ids.Writer.TryWrite(job.Id);
        return givenUp;
    }

    private StoredJob? Load(long id)
    {
        var job = state.GetJob(id);
        return job?.Status == JobStatus.Pending ? job : null;
    }
}
