namespace AiRunner.Core;

public enum JobType { AnalyzeIssue, BreakdownIssue, ImplementIssue, ReviewPullRequest, SequenceSubIssues, FixReview }

/// <summary>
/// One unit of work, small enough to persist in SQLite. Number is the issue or PR number.
/// Payload is job specific (the PR head sha for a review, the @ai comment id for a review fix).
/// </summary>
public sealed record JobRequest(JobType Type, string Repo, int Number, string? Payload = null)
{
    /// <summary>Two requests with the same key are the same work; the queue keeps one pending.</summary>
    public string Key => $"{Type}:{Repo}#{Number}";

    public override string ToString() => Payload is null ? Key : $"{Key} ({Payload})";
}

/// <summary>What the planner decided for one issue or PR: a job, or why not.</summary>
/// <param name="Routine">A check planned on every poll (sequencing); not worth a log line.</param>
/// <param name="NeedsComments">The decision depends on the comments, which were not given.</param>
public sealed record PlanDecision(JobRequest? Job, string Reason, bool Routine = false, bool NeedsComments = false)
{
    public static PlanDecision Run(JobRequest job, string reason) => new(job, reason);
    public static PlanDecision Skip(string reason) => new(null, reason);

    public override string ToString() => Job is null ? $"skip ({Reason})" : $"{Job.Type} ({Reason})";
}
