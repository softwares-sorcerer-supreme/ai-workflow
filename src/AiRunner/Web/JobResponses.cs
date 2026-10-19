using AiRunner.Configuration;
using AiRunner.Hosting;
using AiRunner.Infrastructure;

namespace AiRunner.Web;

/// <summary>An error the API explains in one sentence.</summary>
public sealed record ApiError(string Message);

/// <summary>
/// One job of the history or the queue. State comes as a stable code plus the text to show; the log is read through
/// <c>/api/jobs/{id}/files</c>.
/// </summary>
public sealed record JobResponse(
    long Id,
    string Type,
    string TypeLabel,
    string Status,
    string StatusLabel,
    string Repo,
    int Number,
    string? Project,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    double? DurationSeconds,
    string? Result,
    string? Detail,
    string? Error,
    bool HasLog)
{
    public static JobResponse From(JobRecord job, RunnerConfig config)
    {
        var project = config.FindProject(job.Repo)?.Name;
        double? duration = job.StartedAt is { } startedAt && job.FinishedAt is { } finishedAt
            ? (finishedAt - startedAt).TotalSeconds
            : null;

        return new JobResponse(
            job.Id,
            job.Type,
            JobLabels.TypeLabel(job.Type),
            job.Status,
            JobLabels.StatusLabel(job.Status),
            job.Repo,
            job.Number,
            project,
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
            duration,
            job.Result,
            job.Detail,
            job.Error,
            !string.IsNullOrEmpty(job.LogDir));
    }
}

/// <summary>The job the worker runs now. <c>RoundLabel</c> is set while an agent session is in a round.</summary>
public sealed record RunningJobResponse(
    long Id,
    string Type,
    string TypeLabel,
    string Repo,
    int Number,
    string? Project,
    DateTimeOffset StartedAt,
    double ElapsedSeconds,
    int? Round,
    string? RoundLabel)
{
    public static RunningJobResponse From(RunningJob job, RunnerConfig config, DateTimeOffset now)
    {
        var project = config.FindProject(job.Repo)?.Name;
        var roundLabel = job.Round is { } round ? $"Round {round}" : null;

        return new RunningJobResponse(
            job.Id,
            job.Type,
            JobLabels.TypeLabel(job.Type),
            job.Repo,
            job.Number,
            project,
            job.StartedAt,
            Math.Max(0, (now - job.StartedAt).TotalSeconds),
            job.Round,
            roundLabel);
    }
}

/// <summary><c>GET /api/overview</c>: the running job (null when idle) and the queue in the order jobs will run.</summary>
public sealed record OverviewResponse(RunningJobResponse? RunningJob, string QueueLabel, IReadOnlyList<JobResponse> Queue);

/// <summary><c>GET /api/jobs</c>: one page of the history, newest first.</summary>
public sealed record JobsPageResponse(
    IReadOnlyList<JobResponse> Items,
    int Page,
    int PageSize,
    int Total,
    int TotalPages);

/// <summary>A log file of a job. <c>Format</c> is <c>markdown</c> or <c>text</c>: how the UI shows it.</summary>
public sealed record LogFileResponse(string Name, string Format, long SizeBytes);

public sealed record LogFilesResponse(long JobId, IReadOnlyList<LogFileResponse> Files);

/// <summary>One file's text. When <c>IsTruncated</c>, <c>Text</c> is the first 2 MB and the note says so.</summary>
public sealed record LogFileTextResponse(
    string Name,
    string Format,
    long SizeBytes,
    string Text,
    bool IsTruncated,
    string? TruncatedNote);

/// <summary>Display texts for job codes. The UI shows them as sent.</summary>
public static class JobLabels
{
    public static string TypeLabel(string type) => type switch
    {
        "AnalyzeIssue" => "Analyze issue",
        "BreakdownIssue" => "Break down issue",
        "ImplementIssue" => "Implement issue",
        "ReviewPullRequest" => "Review PR",
        "SequenceSubIssues" => "Sequence sub-issues",
        "FixReview" => "Fix review",
        "FinishIssue" => "Finish issue",
        _ => type,
    };

    public static string StatusLabel(string status) => status switch
    {
        JobStatus.Pending => "Queued",
        JobStatus.Running => "Running",
        JobStatus.Done => "Done",
        JobStatus.Failed => "Failed",
        _ => status,
    };

    public static string QueueLabel(int count) => count switch
    {
        0 => "Queue is empty",
        1 => "1 job queued",
        _ => $"{count} jobs queued",
    };
}

/// <summary>Builds the overview from the runner's status and the job table.</summary>
public sealed class OverviewReader(RunnerStatus status, RunnerConfig config, IJobReader jobs, TimeProvider time)
{
    public OverviewResponse Read()
    {
        var running = status.Snapshot().Running;
        var runningResponse = running is null ? null : RunningJobResponse.From(running, config, time.GetUtcNow());
        var queue = jobs.ListQueue()
            .Select(job => JobResponse.From(job, config))
            .ToList();

        return new OverviewResponse(runningResponse, JobLabels.QueueLabel(queue.Count), queue);
    }
}
