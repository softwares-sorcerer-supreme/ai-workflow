using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Hosting;

public sealed class JobWorkerTests : IDisposable
{
    private readonly JobTestKit _kit = new();

    public JobWorkerTests() => _kit.GitBucket.AddIssue(7, "Export", null, "ai");

    public void Dispose() => _kit.Dispose();

    private sealed class StubJob(Func<Task<JobOutcome>> run) : IJob
    {
        public JobType Type => JobType.AnalyzeIssue;
        public JobInfo Info => new(Workflows.Analyze, "Issue analysis", TimeSpan.FromMinutes(1));
        public Task<JobOutcome> RunAsync(JobRequest request, JobRun r, CancellationToken ct) => run();
    }

    private async Task<string> RunAsync(Func<Task<JobOutcome>> job)
    {
        var (worker, queue, logs) = _kit.CreateWorker(new StubJob(job));
        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));
        await worker.DrainAsync(default);
        return logs;
    }

    [Fact]
    public async Task Failure_posts_a_comment_adds_ai_failed_and_writes_a_cost_row()
    {
        var logs = await RunAsync(() => throw new JobFailedException("no ANALYSIS block"));

        Assert.Contains(Labels.Failed, _kit.GitBucket.LabelsOf(7));
        Assert.Contains("Issue analysis failed: no ANALYSIS block", _kit.GitBucket.CommentsOf(7).Single().Body);
        Assert.Contains("failed", File.ReadAllText(Path.Combine(logs, "cost.csv")));
        Assert.Single(_kit.State.ListJobs(JobStatus.Failed));
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
        File.WriteAllText(Path.Combine(logs, "cost.csv"), "header\n");

        using (new FileStream(Path.Combine(logs, "cost.csv"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // like Excel
        {
            queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));
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
        Assert.False(Directory.Exists(Path.Combine(logs, "jobs")) && Directory.EnumerateDirectories(Path.Combine(logs, "jobs")).Any());
    }
}
