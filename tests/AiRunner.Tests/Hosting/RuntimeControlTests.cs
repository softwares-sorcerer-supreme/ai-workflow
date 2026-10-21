using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Hosting;

/// <summary>The dashboard's runtime controls inside the runner: cancel the running job, pause and resume, poll now.</summary>
public sealed class RuntimeControlTests : IDisposable
{
    private readonly JobTestKit _kit = new(settings => settings.ClaimEnabled = true);

    public RuntimeControlTests() => _kit.CodeHost.AddIssue(7, "Export", null, "ai");

    public void Dispose() => _kit.Dispose();

    /// <summary>A job that runs until its token is cancelled; <c>Started</c> completes once it runs.</summary>
    private sealed class BlockingJob(JobType type = JobType.AnalyzeIssue) : IJob
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobType Type => type;

        public JobInfo Info => type == JobType.AnalyzeIssue
            ? new(Workflows.Analyze, "Issue analysis", TimeSpan.FromMinutes(5))
            : new(Workflows.Fix, "Review fix", TimeSpan.FromMinutes(5), OnPullRequest: true);

        public Task Started => _started.Task;

        public bool HasFailed { get; private set; }

        public async Task<JobOutcome> RunAsync(JobRequest request, JobRun jobRun, CancellationToken cancellationToken)
        {
            _started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return JobOutcome.Ok();
        }

        public void OnFailed(JobRequest request) => HasFailed = true;
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private List<StoredJob> Pending() => _kit.State.ListJobs(JobStatus.Pending).ToList();

    [Fact]
    public async Task A_cancelled_job_fails_like_a_timeout_and_the_next_poll_does_not_queue_it_again()
    {
        var job = new BlockingJob();
        var (worker, queue, logs) = _kit.CreateWorker(job);
        var poller = _kit.NewPoller(queue);
        await poller.PollOnceAsync(default);
        var queued = Assert.Single(Pending());

        var drain = worker.DrainAsync(default);
        await job.Started.WaitAsync(TestTimeout);
        var result = _kit.Commands.Cancel(queued.Id);
        await drain.WaitAsync(TestTimeout);

        Assert.Equal(CancelResult.Cancelled, result);
        Assert.Contains(Labels.Failed, _kit.CodeHost.LabelsOf(7));
        var failureComment = _kit.CodeHost.CommentsOf(7)
            .Single(comment => (comment.Body ?? "").Contains("Issue analysis failed", StringComparison.Ordinal));
        Assert.Contains("Cancelled from the dashboard.", failureComment.Body);
        Assert.DoesNotContain(Labels.Claimed, _kit.CodeHost.LabelsOf(7));
        var claim = Assert.Single(Claims.All(_kit.CodeHost.CommentsOf(7)));
        Assert.Equal(ClaimState.Released, claim.State);
        var failed = _kit.State.GetRecord(queued.Id);
        Assert.Equal(JobStatus.Failed, failed?.Status);
        Assert.Equal("Cancelled from the dashboard.", failed?.Error);
        var costFile = Path.Combine(logs, "cost.csv");
        Assert.Contains("Cancelled from the dashboard.", File.ReadAllText(costFile));

        await poller.PollOnceAsync(default);

        Assert.Empty(Pending());
    }

    [Fact]
    public async Task A_cancelled_pr_job_counts_its_trigger_as_handled_and_gets_no_failed_label()
    {
        _kit.CodeHost.AddPull(5, "feature", "main");
        var job = new BlockingJob(JobType.FixReview);
        var (worker, queue, _) = _kit.CreateWorker(job);
        var request = _kit.Request(JobType.FixReview, 5, payload: "42");
        queue.Enqueue(request);
        var queued = Assert.Single(Pending());

        var drain = worker.DrainAsync(default);
        await job.Started.WaitAsync(TestTimeout);
        _kit.Commands.Cancel(queued.Id);
        await drain.WaitAsync(TestTimeout);

        Assert.True(job.HasFailed);
        Assert.Equal(JobStatus.Failed, _kit.State.GetRecord(queued.Id)?.Status);
        Assert.Equal(JobWorker.CancelledMessage, _kit.State.GetRecord(queued.Id)?.Error);
    }

    [Fact]
    public void Only_the_running_job_can_be_cancelled()
    {
        using var source = new CancellationTokenSource();
        var handle = _kit.Commands.Track(3, source);

        Assert.Equal(CancelResult.NotRunning, _kit.Commands.Cancel(4));
        Assert.False(source.IsCancellationRequested);

        handle.Dispose();

        Assert.Equal(CancelResult.NotRunning, _kit.Commands.Cancel(3));
        Assert.False(source.IsCancellationRequested);
    }

    [Fact]
    public async Task A_paused_worker_takes_no_new_job_while_the_poller_still_queues_and_resume_runs_it()
    {
        var hasRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new StubJob(() =>
        {
            hasRun.TrySetResult();

            return Task.FromResult(JobOutcome.Ok("fine"));
        });
        var (worker, queue, _) = _kit.CreateWorker(job);
        var poller = _kit.NewPoller(queue);
        _kit.Commands.Pause();
        using var stop = new CancellationTokenSource();
        await worker.StartAsync(stop.Token);

        await poller.PollOnceAsync(default);
        await Task.Delay(200);

        Assert.False(hasRun.Task.IsCompleted);
        Assert.Single(Pending());
        Assert.True(_kit.Status.Snapshot().IsPaused);

        _kit.Commands.Resume();
        await hasRun.Task.WaitAsync(TestTimeout);

        Assert.False(_kit.Status.Snapshot().IsPaused);
        await stop.CancelAsync();
        await worker.StopAsync(default);
    }

    private sealed class StubJob(Func<Task<JobOutcome>> run) : IJob
    {
        public JobType Type => JobType.AnalyzeIssue;
        public JobInfo Info => new(Workflows.Analyze, "Issue analysis", TimeSpan.FromMinutes(1));
        public Task<JobOutcome> RunAsync(JobRequest request, JobRun jobRun, CancellationToken cancellationToken) => run();
    }

    [Fact]
    public async Task A_wake_gives_exactly_one_more_poll()
    {
        _kit.Config.PollIntervalSeconds = 3600;
        var (_, queue, _) = _kit.CreateWorker();
        var poller = _kit.NewPoller(queue);
        using var stop = new CancellationTokenSource();

        await poller.StartAsync(stop.Token);
        await WaitForPollsAsync(1);

        _kit.Commands.WakePoller();
        await WaitForPollsAsync(2);
        await Task.Delay(300);

        Assert.Equal(2, IssueListCalls() / IssueListsPerPoll);
        await stop.CancelAsync();
        await poller.StopAsync(default);
    }

    [Fact]
    public async Task Wakes_that_come_before_the_poller_reads_one_give_one_wake()
    {
        _kit.Commands.WakePoller();
        _kit.Commands.WakePoller();
        _kit.Commands.WakePoller();

        await _kit.Commands.WaitForWakeAsync(default).WaitAsync(TestTimeout);
        var second = _kit.Commands.WaitForWakeAsync(default);
        await Task.Delay(100);

        Assert.False(second.IsCompleted);
    }

    /// <summary>Each poll lists the issues twice: with the trigger label and assigned to the runner.</summary>
    private const int IssueListsPerPoll = 2;

    private int IssueListCalls() => _kit.CodeHost.RepoCalls.Count(call => call.Operation == "issues");

    private async Task WaitForPollsAsync(int polls)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (IssueListCalls() < polls * IssueListsPerPoll || _kit.Status.Snapshot().IsPolling)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {polls} polls, saw {IssueListCalls() / IssueListsPerPoll}.");
            }

            await Task.Delay(20);
        }
    }
}
