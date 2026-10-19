namespace AiRunner.Infrastructure;

/// <summary>
/// One row of the jobs table as the dashboard shows it. <c>Result</c> and <c>Detail</c> are how the job ended; they
/// are empty while it is queued or running, and for jobs from before the dashboard.
/// </summary>
public sealed record JobRecord(
    long Id,
    string Type,
    string Repo,
    int Number,
    string Status,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    string? LogDir,
    string? Result,
    string? Detail);

/// <summary>Filters and the page of a job history read. <c>Repos</c> null = every repo, empty = none.</summary>
public sealed record JobQuery(
    string? Status = null,
    string? Type = null,
    IReadOnlyList<string>? Repos = null,
    int Page = 1,
    int PageSize = 25);

public sealed record JobPage(IReadOnlyList<JobRecord> Items, int Total);

/// <summary>What the dashboard may read of the job table. No write method, on purpose.</summary>
public interface IJobReader
{
    /// <summary>Pending jobs in the order the worker takes them.</summary>
    IReadOnlyList<JobRecord> ListQueue();

    /// <summary>Queued and running jobs: the Queued / Running badge of an issue.</summary>
    IReadOnlyList<JobRecord> ListActive();

    /// <summary>All jobs, newest first.</summary>
    JobPage QueryHistory(JobQuery query);

    JobRecord? GetRecord(long id);
}
