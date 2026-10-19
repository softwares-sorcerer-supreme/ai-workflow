using AiRunner.Hosting;
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
    public async Task Retry_hint_names_the_trigger_keyword_of_the_project()
    {
        _kit.Settings.TriggerKeyword = "@bot";
        var timeout = TimeSpan.FromMinutes(1);
        var info = new JobInfo(
            Workflows.Fix,
            "AI review fix",
            timeout,
            OnPullRequest: true,
            Retry: $"Write {JobInfo.KeywordPlaceholder} again.");
        var (worker, queue, _) = _kit.CreateWorker(new StubJob(() => throw new JobFailedException("push rejected"), info));

        var analyzeRequest = _kit.Request(JobType.AnalyzeIssue, 7);
        queue.Enqueue(analyzeRequest);
        await worker.DrainAsync(default);

        Assert.Contains("Write @bot again.", _kit.GitBucket.CommentsOf(7).Single().Body);
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
    public async Task The_worker_shows_busy_while_a_job_runs_and_idle_after_it()
    {
        DateTimeOffset? busySinceDuringJob = null;

        await RunAsync(() =>
        {
            busySinceDuringJob = _kit.Status.Snapshot().WorkerBusySince;

            return Task.FromResult(JobOutcome.Ok("fine"));
        });

        Assert.NotNull(busySinceDuringJob);
        Assert.Null(_kit.Status.Snapshot().WorkerBusySince);
    }

    private sealed class RoundJob(Func<JobRun, Task<JobOutcome>> run) : IJob
    {
        public JobType Type => JobType.AnalyzeIssue;
        public JobInfo Info => new(Workflows.Analyze, "Issue analysis", TimeSpan.FromMinutes(1));
        public Task<JobOutcome> RunAsync(JobRequest request, JobRun jobRun, CancellationToken cancellationToken) =>
            run(jobRun);
    }

    [Fact]
    public async Task The_running_job_and_its_round_are_visible_while_it_runs_and_gone_after_it()
    {
        RunningJob? during = null;
        var (worker, queue, _) = _kit.CreateWorker(new RoundJob(jobRun =>
        {
            during = _kit.Status.Snapshot().Running;
            jobRun.ReportRound(2);
            during = _kit.Status.Snapshot().Running;

            return Task.FromResult(JobOutcome.Ok("fine"));
        }));
        var request = _kit.Request(JobType.AnalyzeIssue, 7);
        queue.Enqueue(request);

        await worker.DrainAsync(default);

        Assert.NotNull(during);
        Assert.Equal("AnalyzeIssue", during.Type);
        Assert.Equal(request.Repo, during.Repo);
        Assert.Equal(7, during.Number);
        Assert.Equal(2, during.Round);
        Assert.Null(_kit.Status.Snapshot().Running);
    }

    [Fact]
    public async Task A_job_that_has_not_reported_a_round_has_none()
    {
        RunningJob? during = null;
        var (worker, queue, _) = _kit.CreateWorker(new RoundJob(_ =>
        {
            during = _kit.Status.Snapshot().Running;

            return Task.FromResult(JobOutcome.Ok("fine"));
        }));
        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));

        await worker.DrainAsync(default);

        Assert.NotNull(during);
        Assert.Null(during.Round);
    }

    [Fact]
    public async Task The_log_folder_of_a_kept_job_is_stored_on_its_row()
    {
        var (worker, queue, _) = _kit.CreateWorker(new RoundJob(jobRun =>
        {
            jobRun.Save("note.txt", "x");

            return Task.FromResult(JobOutcome.Ok("fine"));
        }));
        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));

        await worker.DrainAsync(default);

        var job = Assert.Single(_kit.State.QueryHistory(new JobQuery()).Items);
        Assert.NotNull(job.LogDir);
        Assert.EndsWith("-7-AnalyzeIssue", job.LogDir);
        Assert.Equal("ok", job.Result);
    }

    [Fact]
    public async Task The_worker_is_idle_again_after_a_failed_job()
    {
        await RunAsync(() => throw new JobFailedException("no ANALYSIS block"));

        Assert.Null(_kit.Status.Snapshot().WorkerBusySince);
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
