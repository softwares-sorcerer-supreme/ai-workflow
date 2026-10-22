using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Jobs;

/// <summary>The steps a job writes, in order, through the real job classes and the worker.</summary>
public sealed class JobStepsTests : IDisposable
{
    private readonly JobTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private IEnumerable<string> StepNames => _kit.Steps.Select(step => $"{step.Code}:{step.Label}");

    /// <summary>A job that opens the given steps and then ends as told, like a real job.</summary>
    private sealed class StepJob(Func<JobRun, Task<JobOutcome>> body) : IJob
    {
        public JobType Type => JobType.AnalyzeIssue;

        public JobInfo Info => new(Workflows.Analyze, "Issue analysis", TimeSpan.FromMinutes(1));

        public Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken cancellationToken) =>
            body(run);
    }

    [Fact]
    public async Task An_implement_job_with_a_fix_round_goes_through_its_steps_in_order()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        _kit.CodeHost.AddBotComment(12, Markers.Wrap(Samples.Spec, Markers.Spec));
        _kit.Processes.ExitCodeSequence["make test"] = new Queue<int>([1, 0]);
        var job = new ImplementIssueJob(
            _kit.Config, _kit.CodeHost, _kit.Loader, _kit.Workspace, _kit.Git, _kit.Session, _kit.Attachments);

        await job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);
        _kit.EndRun();

        Assert.Equal(
            [
                "prepare:Prepare workspace",
                "attachments:Attachments",
                "comment:Copilot started",
                "copilot:Copilot round 1",
                "scope-guard:Scope guard",
                "check:build",
                "check:test",
                "scope-guard:Scope guard",
                "copilot:Copilot round 2",
                "scope-guard:Scope guard",
                "check:build",
                "check:test",
                "scope-guard:Scope guard",
                "commit:Commit",
                "push:Push",
                "pull-request:Open PR",
                "comment:Report on the issue",
            ],
            StepNames);
        Assert.All(_kit.Steps, step => Assert.NotNull(step.EndedAt));
        var failedTest = _kit.Steps.Single(step => step.Outcome == JobSteps.Failed);
        Assert.Equal("test", failedTest.Label);
        Assert.Equal("failed (exit 1)", failedTest.Detail);
        Assert.Equal(Enumerable.Range(1, _kit.Steps.Count), _kit.Steps.Select(step => step.Seq));
    }

    [Fact]
    public async Task An_analysis_job_goes_through_its_steps_in_order()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", "Please add CSV export", "ai", Labels.QuestionsOpen);
        _kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("ready", "simple"), Samples.Spec);
        var job = new AnalyzeIssueJob(
            _kit.Config, _kit.CodeHost, _kit.Loader, _kit.Workspace, _kit.Copilot, _kit.State, _kit.Attachments);

        await job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default);
        _kit.EndRun();

        Assert.Equal(
            [
                "prepare:Prepare workspace",
                "attachments:Attachments",
                "copilot:Copilot round 1",
                "comment:Post analysis",
            ],
            StepNames);
        Assert.All(_kit.Steps, step => Assert.Equal(JobSteps.Ok, step.Outcome));
    }

    [Fact]
    public async Task A_copilot_run_writes_its_live_file_next_to_the_saved_output()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", "Please add CSV export", "ai", Labels.QuestionsOpen);
        _kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("ready", "simple"), Samples.Spec);
        var job = new AnalyzeIssueJob(
            _kit.Config, _kit.CodeHost, _kit.Loader, _kit.Workspace, _kit.Copilot, _kit.State, _kit.Attachments);

        await job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default);

        var request = Assert.Single(_kit.Copilot.Requests);
        Assert.Equal(Path.Combine(_kit.Run.Dir, "copilot-live.txt"), request.LiveFile);
        Assert.True(File.Exists(Path.Combine(_kit.Run.Dir, "copilot-stdout.txt")));
    }

    [Fact]
    public async Task A_failed_job_ends_with_a_failed_last_step_that_carries_the_message()
    {
        _kit.CodeHost.AddIssue(7, "Export", null, "ai");
        var job = new StepJob(run =>
        {
            run.Step(JobSteps.Prepare, "Prepare workspace");
            run.Step(JobSteps.Copilot, "Copilot round 1");

            throw new JobFailedException("Copilot exited with code 2");
        });
        var (worker, queue, _) = _kit.CreateWorker(job);

        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));
        await worker.DrainAsync(default);

        var failedJob = Assert.Single(_kit.State.ListJobs(JobStatus.Failed));
        var steps = _kit.State.ListSteps(failedJob.Id);
        Assert.Equal(["prepare", "copilot"], steps.Select(step => step.Code));
        Assert.Equal([JobSteps.Ok, JobSteps.Failed], steps.Select(step => step.Outcome));
        Assert.Equal("Copilot exited with code 2", steps[^1].Detail);
        Assert.All(steps, step => Assert.NotNull(step.EndedAt));
    }

    [Fact]
    public async Task A_finished_job_closes_its_last_step_as_ok_and_the_dashboard_sees_the_current_step_while_it_runs()
    {
        _kit.CodeHost.AddIssue(7, "Export", null, "ai");
        RunningStep? seenWhileRunning = null;
        var job = new StepJob(run =>
        {
            run.Step(JobSteps.Prepare, "Prepare workspace");
            seenWhileRunning = _kit.Status.Snapshot().Running?.Step;

            return Task.FromResult(JobOutcome.Ok("fine"));
        });
        var (worker, queue, _) = _kit.CreateWorker(job);

        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));
        await worker.DrainAsync(default);

        var doneJob = Assert.Single(_kit.State.ListJobs(JobStatus.Done));
        var step = Assert.Single(_kit.State.ListSteps(doneJob.Id));
        Assert.Equal(JobSteps.Ok, step.Outcome);
        Assert.Equal(new RunningStep("prepare", "Prepare workspace"), seenWhileRunning);
        Assert.Null(_kit.Status.Snapshot().Running);
    }

    [Fact]
    public async Task A_job_that_found_nothing_to_do_leaves_no_steps()
    {
        _kit.CodeHost.AddIssue(7, "Export", null, "ai");
        var job = new StepJob(run =>
        {
            run.Step(JobSteps.Prepare, "Prepare workspace");

            return Task.FromResult(JobOutcome.NotNeeded("nothing"));
        });
        var (worker, queue, _) = _kit.CreateWorker(job);

        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));
        await worker.DrainAsync(default);

        Assert.Empty(_kit.State.ListJobs(JobStatus.Done));

        // The first job row of a fresh database is id 1; its row and its steps are gone.
        Assert.Empty(_kit.State.ListSteps(1));
    }

    // ---- the tracker on its own ----

    private sealed class RecordingLog : IJobStepLog
    {
        public List<string> Calls { get; } = [];

        public void ClearSteps(long jobId) => Calls.Add($"clear {jobId}");

        public void OpenStep(JobStepRecord step) => Calls.Add($"open {step.Seq} {step.Code}");

        public void CloseStep(JobStepEnd end) => Calls.Add($"close {end.Seq} {end.Outcome} {end.Detail}");
    }

    private sealed class BrokenLog : IJobStepLog
    {
        public void ClearSteps(long jobId) => throw new InvalidOperationException("database is locked");

        public void OpenStep(JobStepRecord step) => throw new InvalidOperationException("database is locked");

        public void CloseStep(JobStepEnd end) => throw new InvalidOperationException("database is locked");
    }

    [Fact]
    public void A_new_step_closes_the_previous_one_as_ok_and_the_end_closes_the_last()
    {
        var log = new RecordingLog();
        var tracker = new JobStepTracker(5, log, TimeProvider.System, NullLogger.Instance);

        tracker.Open(JobSteps.Prepare, "Prepare");
        tracker.Open(JobSteps.Copilot, "Copilot");
        tracker.End(JobSteps.Failed, "boom");
        tracker.End(JobSteps.Ok, null);

        Assert.Equal(
            ["clear 5", "open 1 prepare", "close 1 ok ", "open 2 copilot", "close 2 failed boom"],
            log.Calls);
    }

    [Fact]
    public void A_store_error_never_reaches_the_job()
    {
        var tracker = new JobStepTracker(5, new BrokenLog(), TimeProvider.System, NullLogger.Instance);
        var opened = new List<string>();
        var trackerWithCallback = new JobStepTracker(5, new BrokenLog(), TimeProvider.System, NullLogger.Instance)
        {
            Opened = (code, _) => opened.Add(code),
        };

        tracker.Open(JobSteps.Prepare, "Prepare");
        tracker.End(JobSteps.Failed, "boom");
        trackerWithCallback.Open(JobSteps.Copilot, "Copilot");

        Assert.Equal(["copilot"], opened);
    }

    [Fact]
    public void A_run_without_a_tracker_ignores_steps()
    {
        var run = new JobRun(Path.Combine(Path.GetTempPath(), "ai-runner-no-tracker"), NullLogger.Instance);

        run.Step(JobSteps.Prepare, "Prepare");
        run.EndStep(JobSteps.Failed, "boom");

        Assert.False(Directory.Exists(run.Dir));
    }

    [Theory]
    [InlineData(JobSteps.Ok, true, JobSteps.Ok)]
    [InlineData(JobSteps.Failed, false, JobSteps.Failed)]
    [InlineData(null, true, JobSteps.Running)]
    [InlineData(null, false, JobSteps.Interrupted)]
    public void A_step_state_is_its_outcome_else_running_or_interrupted(
        string? outcome,
        bool isJobRunning,
        string expected)
    {
        Assert.Equal(expected, JobSteps.StateOf(outcome, isJobRunning));
    }
}
