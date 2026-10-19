namespace AiRunner.Core;

/// <summary>What the health rule looks at; all times UTC.</summary>
/// <param name="LastPollEndedAt">End of the last finished poll, or null before the first one.</param>
/// <param name="FailingProjects">Projects whose latest poll failed.</param>
public sealed record HealthInput(
    DateTimeOffset Now,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastPollEndedAt,
    TimeSpan PollInterval,
    int FailingProjects);

/// <summary>A stable code for the UI to style by, and the text to show.</summary>
public sealed record HealthState(string Code, string Label);

/// <summary>
/// Whether the runner is working (the **Health** of CONTEXT.md). Lives here, not in the UI, so "idle" and "broken"
/// are told apart by one tested rule. First match wins: a runner that stopped polling is stale even when its last
/// poll had errors, because those errors may be long gone.
/// </summary>
public static class RunnerHealth
{
    public const string Starting = "starting";
    public const string Stale = "stale";
    public const string Error = "error";
    public const string Ok = "ok";

    /// <summary>A poll is late after this many intervals; one slow GitBucket answer is not "stale" yet.</summary>
    public const int StaleAfterIntervals = 3;

    public static HealthState Assess(HealthInput input)
    {
        var staleAfter = input.PollInterval * StaleAfterIntervals;

        if (input.LastPollEndedAt is not { } lastPollEnded)
        {
            var isStillStarting = input.Now - input.StartedAt <= staleAfter;

            return isStillStarting
                ? new HealthState(Starting, "Waiting for the first poll")
                : new HealthState(Stale, "The first poll has not finished");
        }

        var sinceLastPoll = input.Now - lastPollEnded;
        if (sinceLastPoll > staleAfter)
        {
            var minutes = (int)sinceLastPoll.TotalMinutes;

            return new HealthState(Stale, minutes < 1 ? "No poll for a while" : $"No poll for {minutes} min");
        }

        if (input.FailingProjects > 0)
        {
            var projects = input.FailingProjects == 1 ? "1 project" : $"{input.FailingProjects} projects";

            return new HealthState(Error, $"Poll failed in {projects}");
        }

        return new HealthState(Ok, "Healthy");
    }
}
