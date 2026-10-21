using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;

namespace AiRunner.Web;

/// <summary>An error the API explains in one sentence.</summary>
public sealed record ApiError(string Message);

/// <summary>The answer of a write: what the runner will do now, ready to show.</summary>
public sealed record CommandResponse(string Message);

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
    bool HasLog,
    int? CopilotCalls)
{
    /// <summary>The steps the job went through; only <c>GET /api/jobs/{id}</c> fills it (lists leave it out).</summary>
    public IReadOnlyList<JobStepResponse>? Steps { get; init; }

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
            !string.IsNullOrEmpty(job.LogDir),
            job.CopilotCalls);
    }
}

/// <summary>
/// One step of a job. <c>State</c> is <c>running | ok | failed | interrupted</c> with its label; <c>Detail</c> says
/// why a step failed (a check's exit code, the failure message).
/// </summary>
public sealed record JobStepResponse(
    int Seq,
    string Code,
    string Label,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    double? DurationSeconds,
    string State,
    string StateLabel,
    string? Detail)
{
    public static JobStepResponse From(JobStepRecord step, bool isJobRunning)
    {
        var state = JobSteps.StateOf(step.Outcome, isJobRunning);
        double? duration = step.EndedAt is { } endedAt ? Math.Max(0, (endedAt - step.StartedAt).TotalSeconds) : null;

        return new JobStepResponse(
            step.Seq,
            step.Code,
            step.Label,
            step.StartedAt,
            step.EndedAt,
            duration,
            state,
            JobSteps.StateLabel(state),
            step.Detail);
    }

    public static List<JobStepResponse> All(IEnumerable<JobStepRecord> steps, bool isJobRunning) =>
        steps
            .Select(step => From(step, isJobRunning))
            .ToList();
}

/// <summary>
/// The job the worker runs now. <c>RoundLabel</c> is set while an agent session is in a round; <c>CurrentStep</c>
/// names the step it is in, and <c>Steps</c> lists the steps so far.
/// </summary>
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
    string? RoundLabel,
    CodeLabelResponse? CurrentStep,
    IReadOnlyList<JobStepResponse> Steps)
{
    public static RunningJobResponse From(RunningJob job, RunnerConfig config, DateTimeOffset now)
    {
        var project = config.FindProject(job.Repo)?.Name;
        var roundLabel = job.Round is { } round ? $"Round {round}" : null;
        var currentStep = job.Step is { } step ? CodeLabelResponse.From(new CodeLabel(step.Code, step.Label)) : null;

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
            roundLabel,
            currentStep,
            []);
    }
}

/// <summary>
/// One open issue that waits on a human. <c>Action</c> says what to do (code plus text); the UI shows it as sent.
/// </summary>
public sealed record WaitingItemResponse(
    string Project,
    string Repo,
    int Number,
    string Title,
    CodeLabelResponse Stage,
    IReadOnlyList<CodeLabelResponse> Flags,
    CodeLabelResponse Action,
    string? HtmlUrl,
    DateTimeOffset UpdatedAt);

/// <summary>
/// <c>GET /api/overview</c>: the running job (null when idle), the queue in the order jobs will run, and the issues
/// that wait on a human, longest-waiting first.
/// </summary>
public sealed record OverviewResponse(
    RunningJobResponse? RunningJob,
    string QueueLabel,
    IReadOnlyList<JobResponse> Queue,
    string WaitingLabel,
    IReadOnlyList<WaitingItemResponse> WaitingOn);

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

/// <summary>
/// One file's text from byte <c>Offset</c> (0 unless asked). When <c>IsTruncated</c>, <c>Text</c> is the first 2 MB
/// from there and the note says so. A tail asks again with <c>NextOffset</c>; text is always whole characters.
/// </summary>
public sealed record LogFileTextResponse(
    string Name,
    string Format,
    long SizeBytes,
    string Text,
    bool IsTruncated,
    string? TruncatedNote)
{
    public long Offset { get; init; }

    public long NextOffset { get; init; }
}

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

    public static string WaitingLabel(int count) => count switch
    {
        0 => "Nothing waits on you",
        1 => "1 issue waits on you",
        _ => $"{count} issues wait on you",
    };

    public static string QueueLabel(int count) => count switch
    {
        0 => "Queue is empty",
        1 => "1 job queued",
        _ => $"{count} jobs queued",
    };
}

/// <summary>Builds the overview from the runner's status, the job table and the stage of every issue.</summary>
public sealed class OverviewReader(
    RunnerStatus status,
    RunnerConfig config,
    IJobReader jobs,
    IssueBoardReader boardReader,
    TimeProvider time)
{
    public OverviewResponse Read()
    {
        var running = status.Snapshot().Running;
        RunningJobResponse? runningResponse = null;
        if (running is not null)
        {
            var steps = JobStepResponse.All(jobs.ListSteps(running.Id), isJobRunning: true);
            runningResponse = RunningJobResponse.From(running, config, time.GetUtcNow()) with { Steps = steps };
        }

        var queue = jobs.ListQueue()
            .Select(job => JobResponse.From(job, config))
            .ToList();
        var waiting = WaitingItems();

        return new OverviewResponse(
            runningResponse,
            JobLabels.QueueLabel(queue.Count),
            queue,
            JobLabels.WaitingLabel(waiting.Count),
            waiting);
    }

    private List<WaitingItemResponse> WaitingItems()
    {
        var board = boardReader.Read();

        return board.Entries.Values
            .Where(entry => entry.Waiting is not null)
            .OrderBy(entry => entry.Issue.UpdatedAt)
            .ThenBy(entry => entry.Issue.Number)
            .Select(entry => WaitingItemOf(entry, entry.Waiting!))
            .ToList();
    }

    private static WaitingItemResponse WaitingItemOf(IssueEntry entry, CodeLabel action)
    {
        var flags = entry.Stage.Flags
            .Select(CodeLabelResponse.From)
            .ToList();

        return new WaitingItemResponse(
            entry.Project.Name,
            entry.Issue.Repo,
            entry.Issue.Number,
            entry.Issue.Title,
            CodeLabelResponse.From(entry.Stage.Stage),
            flags,
            CodeLabelResponse.From(action),
            entry.Issue.HtmlUrl,
            entry.Issue.UpdatedAt);
    }
}
