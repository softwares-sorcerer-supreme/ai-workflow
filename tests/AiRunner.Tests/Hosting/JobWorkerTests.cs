using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Hosting;

public sealed class JobWorkerTests : IDisposable
{
    private readonly JobTestKit _kit = new();

    public JobWorkerTests() => _kit.CodeHost.AddIssue(7, "Export", null, "ai");

    public void Dispose() => _kit.Dispose();

    private async Task<string> RunAsync(Func<Task<JobOutcome>> job)
    {
        var (worker, queue, logs) = _kit.CreateWorker(new StubJob(job));
        var analyzeRequest = _kit.Request(JobType.AnalyzeIssue, 7);
        queue.Enqueue(analyzeRequest);
        await worker.DrainAsync(default);

        return logs;
    }

    /// <summary>The real store, except that storing a job's result fails (database locked, disk error).</summary>
    private sealed class ResultFailingStore(IStateStore inner) : IStateStore
    {
        public IReadOnlyDictionary<int, string> GetIssueFingerprints(string repo) => inner.GetIssueFingerprints(repo);

        public void SetIssueFingerprint(string repo, int issue, string fingerprint) =>
            inner.SetIssueFingerprint(repo, issue, fingerprint);

        public long GetLastHandledComment(string repo, int issue) => inner.GetLastHandledComment(repo, issue);

        public void SetLastHandledComment(string repo, int issue, long commentId) =>
            inner.SetLastHandledComment(repo, issue, commentId);

        public ReviewState? GetReview(string repo, int pullNumber) => inner.GetReview(repo, pullNumber);

        public void SetReview(string repo, int pullNumber, ReviewState reviewState) =>
            inner.SetReview(repo, pullNumber, reviewState);

        public long AddJob(JobRequest request) => inner.AddJob(request);

        public StoredJob? GetJob(long id) => inner.GetJob(id);

        public StoredJob? FindJob(string key, string status) => inner.FindJob(key, status);

        public IReadOnlyList<StoredJob> ListJobs(string status) => inner.ListJobs(status);

        public void UpdateJobPayload(long id, string? payload) => inner.UpdateJobPayload(id, payload);

        public void SetJobStatus(long id, string status, string? error = null) =>
            inner.SetJobStatus(id, status, error);

        public void PruneJobs(DateTimeOffset finishedBefore) => inner.PruneJobs(finishedBefore);

        public void DeleteJob(long id) => inner.DeleteJob(id);

        public void AddCost(CostEntry entry) => inner.AddCost(entry);

        public void SetJobLogDir(long id, string folderName) => inner.SetJobLogDir(id, folderName);

        public void SetJobResult(long id, JobResult jobResult) => throw new InvalidOperationException("database is locked");
    }

    [Fact]
    public async Task A_result_that_cannot_be_stored_does_not_turn_success_into_failure()
    {
        var job = new StubJob(() => Task.FromResult(JobOutcome.Ok("fine")));
        var (worker, queue, _) = _kit.CreateWorker(new ResultFailingStore(_kit.State), job);

        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));
        await worker.DrainAsync(default);

        Assert.Empty(_kit.CodeHost.CommentsOf(7));
        Assert.DoesNotContain(Labels.Failed, _kit.CodeHost.LabelsOf(7));
        Assert.Single(_kit.State.ListJobs(JobStatus.Done));
        Assert.Single(_kit.State.ListCost());
    }

    [Fact]
    public async Task A_result_that_cannot_be_stored_does_not_stop_the_failure_report()
    {
        var job = new StubJob(() => throw new JobFailedException("no ANALYSIS block"));
        var (worker, queue, _) = _kit.CreateWorker(new ResultFailingStore(_kit.State), job);

        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));
        await worker.DrainAsync(default);

        Assert.Contains(Labels.Failed, _kit.CodeHost.LabelsOf(7));
        Assert.Contains("Issue analysis failed: no ANALYSIS block", _kit.CodeHost.CommentsOf(7).Single().Body);
    }

    [Fact]
    public async Task Failure_posts_a_comment_adds_ai_failed_and_writes_a_cost_row()
    {
        var logs = await RunAsync(() => throw new JobFailedException("no ANALYSIS block"));

        Assert.Contains(Labels.Failed, _kit.CodeHost.LabelsOf(7));
        Assert.Contains("Issue analysis failed: no ANALYSIS block", _kit.CodeHost.CommentsOf(7).Single().Body);
        var costFile = Path.Combine(logs, "cost.csv");
        Assert.Contains("failed", File.ReadAllText(costFile));
        Assert.Single(_kit.State.ListJobs(JobStatus.Failed));
    }

    [Fact]
    public async Task Copilot_calls_of_the_outcome_reach_the_cost_row_the_csv_and_the_job_row()
    {
        var logs = await RunAsync(() => Task.FromResult(JobOutcome.Ok("round 1", copilotCalls: 3)));

        var cost = Assert.Single(_kit.State.ListCost());
        Assert.Equal(3, cost.CopilotCalls);
        Assert.EndsWith(",3,,,,,", File.ReadAllLines(Path.Combine(logs, "cost.csv"))[1]);
        Assert.Equal(3, _kit.State.QueryHistory(new JobQuery()).Items.Single().CopilotCalls);
    }

    /// <summary>Writes the given usage files into its run folder, as the Copilot CLI does, then ends.</summary>
    /// <param name="ending">"ok", "failed" (throws) or "quiet" (a not-needed outcome).</param>
    private sealed class UsageWritingJob(Dictionary<string, string> usageFiles, string ending) : IJob
    {
        public JobType Type => JobType.AnalyzeIssue;
        public JobInfo Info => new(Workflows.Analyze, "Issue analysis", TimeSpan.FromMinutes(1));

        public Task<JobOutcome> RunAsync(JobRequest request, JobRun jobRun, CancellationToken cancellationToken)
        {
            foreach (var (name, json) in usageFiles)
            {
                jobRun.Save(name, json);
            }

            return ending switch
            {
                "failed" => throw new JobFailedException("no ANALYSIS block"),
                "quiet" => Task.FromResult(JobOutcome.NotNeeded("nothing to do after all")),
                _ => Task.FromResult(JobOutcome.Ok("round 2", copilotCalls: 2)),
            };
        }
    }

    /// <summary>A usage file whose one model (sonnet, agent main) spent all of it.</summary>
    private static string UsageJson(long nanoAiu, long output)
    {
        var usage = $$""" "totalNanoAiu": {{nanoAiu}}, "tokenDetails": { "output": { "tokenCount": {{output}} } } """;

        return $$"""{ {{usage}}, "modelMetrics": { "sonnet": { {{usage}} } } }""";
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("failed")]
    [InlineData("quiet")]   // the job row goes, the cost Copilot billed stays
    public async Task The_usage_files_of_every_round_are_added_up_in_the_cost_row(string ending)
    {
        var usageFiles = new Dictionary<string, string>
        {
            ["round-1-copilot-usage.json"] = UsageJson(100, 10),
            ["round-2-copilot-usage.json"] = UsageJson(50, 5),
            ["round-3-copilot-usage.json"] = "not json",
        };
        var (worker, queue, _) = _kit.CreateWorker(new UsageWritingJob(usageFiles, ending));

        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));
        await worker.DrainAsync(default);

        var cost = Assert.Single(_kit.State.ListCost(withParts: true));
        Assert.Equal(new CopilotUsage(150, 0, 0, 0, 15), cost.Usage);
        Assert.Equal(["round-1-copilot", "round-2-copilot"], cost.Parts.Select(part => part.Run));
    }

    [Fact]
    public async Task A_job_without_usage_files_has_no_usage()
    {
        await RunAsync(() => Task.FromResult(JobOutcome.Ok("round 1", copilotCalls: 1)));

        Assert.Null(Assert.Single(_kit.State.ListCost()).Usage);
    }

    [Fact]
    public async Task A_failed_job_has_no_known_copilot_calls()
    {
        await RunAsync(() => throw new JobFailedException("no ANALYSIS block"));

        Assert.Null(Assert.Single(_kit.State.ListCost()).CopilotCalls);
        Assert.Null(_kit.State.QueryHistory(new JobQuery()).Items.Single().CopilotCalls);
    }

    [Fact]
    public async Task The_project_lock_a_job_took_is_released_when_it_ends_even_when_it_fails()
    {
        var holdsAtEnd = new List<bool>();
        var (worker, queue, _) = _kit.CreateWorker(new StubJob(async () =>
        {
            await _kit.Locks.HoldForJobAsync("mbh", default);
            holdsAtEnd.Add(_kit.Locks.IsHeld("mbh"));

            throw new JobFailedException("push rejected");
        }));
        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));

        await worker.DrainAsync(default);

        Assert.Equal([true], holdsAtEnd);
        Assert.False(_kit.Locks.IsHeld("mbh"));
    }

    [Fact]
    public async Task The_project_lock_is_released_after_a_job_that_succeeds()
    {
        var (worker, queue, _) = _kit.CreateWorker(new StubJob(async () =>
        {
            await _kit.Locks.HoldForJobAsync("mbh", default);

            return JobOutcome.Ok("fine");
        }));
        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));

        await worker.DrainAsync(default);

        Assert.False(_kit.Locks.IsHeld("mbh"));
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

        var body = _kit.CodeHost.CommentsOf(7).Single().Body;
        Assert.Contains("AI review fix failed: push rejected", body);
        Assert.Contains("Write @ai again.", body);
        Assert.DoesNotContain("next commit", body);
        Assert.DoesNotContain(Labels.Failed, _kit.CodeHost.LabelsOf(7));
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

        Assert.Contains("Write @bot again.", _kit.CodeHost.CommentsOf(7).Single().Body);
    }

    [Fact]
    public async Task Http_timeout_is_reported_as_a_gitbucket_problem_not_a_job_timeout()
    {
        await RunAsync(() => throw new TaskCanceledException("HttpClient.Timeout"));

        Assert.Contains("GitBucket timed out", _kit.CodeHost.CommentsOf(7).Single().Body);
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

        Assert.Empty(_kit.CodeHost.CommentsOf(7));
        Assert.DoesNotContain(Labels.Failed, _kit.CodeHost.LabelsOf(7));
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
