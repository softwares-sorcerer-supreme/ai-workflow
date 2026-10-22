using AiRunner.Infrastructure;
using AiRunner.Jobs;

namespace AiRunner.Tests.Fakes;

/// <summary>
/// An analysis job that runs <paramref name="run"/>, for worker tests that care about the worker, not the job.
/// </summary>
public sealed class StubJob(Func<Task<JobOutcome>> run, JobInfo? info = null) : IJob
{
    public JobType Type => JobType.AnalyzeIssue;

    public JobInfo Info => info ?? new(Workflows.Analyze, "Issue analysis", TimeSpan.FromMinutes(1));

    public bool HasRun { get; private set; }

    public Task<JobOutcome> RunAsync(JobRequest request, JobRun jobRun, CancellationToken cancellationToken)
    {
        HasRun = true;

        return run();
    }
}
