using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Hosting;

public sealed class JobQueueTests : IDisposable
{
    private readonly JobTestKit _kit = new();

    private JobQueue NewQueue() => new(_kit.State, NullLogger<JobQueue>.Instance);

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void Same_key_is_pending_once_and_keeps_the_newest_payload()
    {
        var queue = NewQueue();

        var firstRequest = _kit.Request(JobType.ReviewPullRequest, 3, "sha1");
        Assert.True(queue.Enqueue(firstRequest));
        var secondRequest = _kit.Request(JobType.ReviewPullRequest, 3, "sha2");
        Assert.False(queue.Enqueue(secondRequest));

        Assert.True(queue.TryDequeue(out var job));
        Assert.Equal("sha2", job!.Request.Payload);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void Interrupted_job_is_retried_once_then_given_up()
    {
        var queue = NewQueue();
        var analyzeRequest = _kit.Request(JobType.AnalyzeIssue, 7);
        queue.Enqueue(analyzeRequest);
        queue.TryDequeue(out var job);
        _kit.State.SetJobStatus(job!.Id, JobStatus.Running);   // crash while running

        var restarted = NewQueue();

        Assert.Empty(restarted.Recover());
        Assert.True(restarted.TryDequeue(out var again));
        _kit.State.SetJobStatus(again!.Id, JobStatus.Running);  // crash again

        var givenUp = NewQueue().Recover();
        Assert.Equal(7, Assert.Single(givenUp).Request.Number);
    }
}
