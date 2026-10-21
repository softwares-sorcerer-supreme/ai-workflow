namespace AiRunner.Core;

/// <summary>
/// The steps a running job goes through, as the dashboard shows them. A step is a log line with a start and an end,
/// not a decision: no part of the workflow reads it back.
/// </summary>
public static class JobSteps
{
    public const string Claim = "claim";
    public const string Prepare = "prepare";
    public const string Attachments = "attachments";
    public const string Copilot = "copilot";
    public const string Check = "check";
    public const string ScopeGuard = "scope-guard";
    public const string Commit = "commit";
    public const string Push = "push";
    public const string PullRequest = "pull-request";
    public const string Comment = "comment";
    public const string Close = "close";

    /// <summary>How a closed step ended.</summary>
    public const string Ok = "ok";

    public const string Failed = "failed";

    /// <summary>States that have no stored outcome: the step is still open.</summary>
    public const string Running = "running";

    /// <summary>The step never closed and its job is not running any more (the runner was stopped).</summary>
    public const string Interrupted = "interrupted";

    /// <summary>
    /// The state of a step: its stored outcome, else running while its job runs, else interrupted. A job that ended
    /// always closes its last step, so an open step of an ended job means the runner stopped in the middle of it.
    /// </summary>
    public static string StateOf(string? outcome, bool isJobRunning)
    {
        if (outcome is not null)
        {
            return outcome;
        }

        return isJobRunning ? Running : Interrupted;
    }

    public static string StateLabel(string state) => state switch
    {
        Ok => "Done",
        Failed => "Failed",
        Running => "Running",
        Interrupted => "Interrupted",
        _ => state,
    };
}
