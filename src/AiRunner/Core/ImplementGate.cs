namespace AiRunner.Core;

public enum ImplementStep
{
    /// <summary>Checks pass and PR_BODY.md is complete: open the PR.</summary>
    Done,
    /// <summary>Something is wrong and a fix round is left: run Copilot again with fix.md.</summary>
    Fix,
    /// <summary>Still wrong, no fix round left: open the PR (WIP when checks fail).</summary>
    OutOfRounds,
}

/// <summary>Where the implement loop stands after the agent stopped and the runner re-ran the checks.</summary>
/// <param name="CanRunAgain">
/// False when the last Copilot call failed (quota, timeout) or the job has too little time left:
/// then the PR is opened from what exists rather than risking the work.
/// </param>
public sealed record ImplementProgress(
    bool ChecksOk,
    int PrBodyProblems,
    int FixRoundsUsed,
    int MaxFixRounds,
    bool CanRunAgain);

/// <summary>
/// The runner's safety net after the agent stopped. The agent is told to verify, review and write
/// PR_BODY.md itself; the runner only checks the result and decides whether one more round is worth a premium request.
/// </summary>
public static class ImplementGate
{
    /// <summary>
    /// When <see cref="ImplementProgress.CanRunAgain"/> is false, the PR is opened from what exists.
    /// When only PR_BODY.md is wrong (checks pass), one fix round is enough; the PR lists what is still missing.
    /// </summary>
    public static ImplementStep Decide(ImplementProgress progress)
    {
        var (checksOk, prBodyProblems, fixRoundsUsed, maxFixRounds, canRunAgain) = progress;

        return checksOk && prBodyProblems == 0 ? ImplementStep.Done
            : !canRunAgain || fixRoundsUsed >= maxFixRounds ? ImplementStep.OutOfRounds
            : checksOk && fixRoundsUsed >= 1 ? ImplementStep.OutOfRounds
            : ImplementStep.Fix;
    }

    /// <summary>
    /// A fix round runs Copilot and the checks again, each up to the per-call timeout. Starting one with
    /// less time left would let the job timeout cancel it mid-way, and a cancelled job saves nothing.
    /// </summary>
    public static bool TimeForAnotherRound(TimeSpan left, TimeSpan perCall) => left >= perCall * 2;
}
