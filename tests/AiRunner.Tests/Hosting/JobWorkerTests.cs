using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Hosting;

public sealed class JobWorkerTests : IDisposable
{
    private readonly JobTestKit _kit = new();

    public JobWorkerTests() => _kit.GitBucket.AddIssue(7, "Export", null, "ai");

    public void Dispose() => _kit.Dispose();

    private sealed class StubJob(Func<Task<JobOutcome>> run, JobInfo? info = null) : IJob
    {
        public JobType Type => JobType.AnalyzeIssue;
        public JobInfo Info => info ?? new(Workflows.Analyze, "Issue analysis", TimeSpan.FromMinutes(1));
        public Task<JobOutcome> RunAsync(JobRequest request, JobRun jobRun, CancellationToken cancellationToken) => run();
    }

    private async Task<string> RunAsync(Func<Task<JobOutcome>> job)
    {
        var (worker, queue, logs) = _kit.CreateWorker(new StubJob(job));
        var analyzeRequest = _kit.Request(JobType.AnalyzeIssue, 7);
        queue.Enqueue(analyzeRequest);
        await worker.DrainAsync(default);

        return logs;
    }

    [Fact]
    public async Task Failure_posts_a_comment_adds_ai_failed_and_writes_a_cost_row()
    {
        var logs = await RunAsync(() => throw new JobFailedException("no ANALYSIS block"));

        Assert.Contains(Labels.Failed, _kit.GitBucket.LabelsOf(7));
        Assert.Contains("Issue analysis failed: no ANALYSIS block", _kit.GitBucket.CommentsOf(7).Single().Body);
        var costFile = Path.Combine(logs, "cost.csv");
        Assert.Contains("failed", File.ReadAllText(costFile));
        Assert.Single(_kit.State.ListJobs(JobStatus.Failed));
    }

    [Fact]
    public async Task Failure_comment_uses_the_jobs_own_retry_hint()
    {
        var timeout = TimeSpan.FromMinutes(1);
        var info = new JobInfo(
            Workflows.Fix,
            "AI review fix",
            timeout,
            OnPullRequest: true,
            Retry: "Write @ai again.");
        var (worker, queue, _) = _kit.CreateWorker(new StubJob(() => throw new JobFailedException("push rejected"), info));

        var analyzeRequest = _kit.Request(JobType.AnalyzeIssue, 7);
        queue.Enqueue(analyzeRequest);
        await worker.DrainAsync(default);

        var body = _kit.GitBucket.CommentsOf(7).Single().Body;
        Assert.Contains("AI review fix failed: push rejected", body);
        Assert.Contains("Write @ai again.", body);
        Assert.DoesNotContain("next commit", body);
        Assert.DoesNotContain(Labels.Failed, _kit.GitBucket.LabelsOf(7));
    }

    [Fact]
    public async Task Http_timeout_is_reported_as_a_gitbucket_problem_not_a_job_timeout()
    {
        await RunAsync(() => throw new TaskCanceledException("HttpClient.Timeout"));

        Assert.Contains("GitBucket timed out", _kit.GitBucket.CommentsOf(7).Single().Body);
    }

    [Fact]
    public async Task Locked_cost_csv_does_not_turn_success_into_failure()
    {
        var (worker, queue, logs) = _kit.CreateWorker(new StubJob(() => Task.FromResult(JobOutcome.Ok("fine"))));
        var costFile = Path.Combine(logs, "cost.csv");
        File.WriteAllText(costFile, "header\n");

        using (new FileStream(costFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // like Excel
        {
            var analyzeRequest = _kit.Request(JobType.AnalyzeIssue, 7);
            queue.Enqueue(analyzeRequest);
            await worker.DrainAsync(default);
        }

        Assert.Empty(_kit.GitBucket.CommentsOf(7));
        Assert.DoesNotContain(Labels.Failed, _kit.GitBucket.LabelsOf(7));
        Assert.Single(_kit.State.ListJobs(JobStatus.Done));
    }

    [Fact]
    public async Task Job_that_finds_nothing_to_do_leaves_no_row_and_no_log_folder()
    {
        var logs = await RunAsync(() => Task.FromResult(JobOutcome.NotNeeded("waiting")));

        Assert.Empty(_kit.State.ListJobs(JobStatus.Done));

        var jobsFolder = Path.Combine(logs, "jobs");
        Assert.False(Directory.Exists(jobsFolder) && Directory.EnumerateDirectories(jobsFolder).Any());
    }
}
