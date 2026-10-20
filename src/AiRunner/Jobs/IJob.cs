using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// One workflow step. A job orchestrates: it reads GitBucket, asks Core to decide or parse,
/// and calls Infrastructure for every side effect. Jobs are stateless; all input is in the
/// request and in GitBucket, so a job can be retried after a restart.
/// </summary>
public interface IJob
{
    JobType Type { get; }

    /// <summary>What the worker needs to know about this kind of job.</summary>
    JobInfo Info { get; }

    /// <summary>
    /// Runs the job. Throw JobFailedException for an expected failure with a message for humans;
    /// the worker posts it, adds ai:failed (issues only) and records the cost row.
    /// </summary>
    Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken cancellationToken);

    /// <summary>Called by the worker after a failure (not on shutdown), before it reports it.</summary>
    void OnFailed(JobRequest request)
    {
    }
}

/// <param name="Workflow">Name in the cost log (repos.yml workflow name).</param>
/// <param name="Description">Start of the failure comment, e.g. "Issue analysis".</param>
/// <param name="Timeout">Whole job, including git and API calls around Copilot.</param>
/// <param name="OnPullRequest">
/// The request number is a PR. A failure is then only commented: no ai:failed label, the job
/// itself remembers the failed commit and the next commit retries.
/// </param>
/// <param name="Retry">
/// How a human retries after a failure, for the failure comment; null = the default for issues or PRs.
/// <see cref="JobInfo.KeywordPlaceholder"/> in it becomes the project's trigger keyword.
/// </param>
/// <param name="Routine">
/// Planned on every poll and usually finds nothing to do. With claims on, such a job claims through
/// <see cref="JobRun.ClaimAsync"/> just before its first write instead of up front.
/// </param>
public sealed record JobInfo(
    string Workflow,
    string Description,
    TimeSpan Timeout,
    bool OnPullRequest = false,
    string? Retry = null,
    bool Routine = false)
{
    public const string KeywordPlaceholder = "{keyword}";
}

/// <param name="Result">Short word for the cost log: ok, wip, red (review fix not pushed), rejected, too-big, not-needed, skipped.</param>
/// <param name="Quiet">Nothing was spent and nothing changed; no cost row, no job row.</param>
/// <param name="CopilotCalls">Copilot calls the job made; null when unknown (a failed job, an old row).</param>
public sealed record JobOutcome(string Result, string Detail = "", bool Quiet = false, int? CopilotCalls = null)
{
    public static JobOutcome Ok(string detail = "", int? copilotCalls = null) =>
        new("ok", detail, CopilotCalls: copilotCalls);
    public static JobOutcome NotNeeded(string reason) => new("not-needed", reason, Quiet: true);
}

/// <summary>An expected failure. The message is posted on the issue, so keep it short and clear.</summary>
public sealed class JobFailedException(string message) : Exception(message);

/// <summary>Finds the job class for a request. All jobs are registered in DI as IJob.</summary>
public sealed class JobFactory(IEnumerable<IJob> jobs)
{
    private readonly Dictionary<JobType, IJob> _byType = jobs.ToDictionary(job => job.Type);

    public IJob Get(JobType type) =>
        _byType.TryGetValue(type, out var job)
            ? job
            : throw new InvalidOperationException($"No job registered for {type}");
}
