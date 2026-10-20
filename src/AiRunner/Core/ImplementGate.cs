using System.Text;
using System.Text.RegularExpressions;

namespace AiRunner.Core;

public enum ImplementStep
{
    /// <summary>Checks pass and PR_BODY.md is complete: open the PR.</summary>
    Done,
    /// <summary>Something is wrong and a fix round is left: run Copilot again with fix.md.</summary>
    Fix,
    /// <summary>Still wrong, no fix round left: open the PR (WIP when checks fail).</summary>
    OutOfRounds,
    /// <summary>
    /// The last fix round ended exactly where the round before it did: another one would most likely do the same, so
    /// the PR is opened like <see cref="OutOfRounds"/>.
    /// </summary>
    NoProgress,
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
    bool CanRunAgain)
{
    /// <summary>
    /// The round's <see cref="ImplementGate.Fingerprint"/> equals the one of the round before (same failing checks with
    /// the same output, same output-file problems).
    /// </summary>
    public bool IsSameAsLastRound { get; init; }
}

/// <summary>
/// The runner's safety net after the agent stopped. The agent is told to verify, review and write
/// PR_BODY.md itself; the runner only checks the result and decides whether one more round is worth a Copilot run.
/// </summary>
public static class ImplementGate
{
    private static readonly Regex Timestamp = new(
        @"\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?)?|\b\d{1,2}:\d{2}:\d{2}(\.\d+)?\b",
        RegexOptions.Compiled);

    private static readonly Regex Duration = new(
        @"\b\d+(\.\d+)?\s?(ms|s|sec|secs|seconds|min|mins|minutes)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// When <see cref="ImplementProgress.CanRunAgain"/> is false, the PR is opened from what exists.
    /// When only PR_BODY.md is wrong (checks pass), one fix round is enough; the PR lists what is still missing.
    /// When checks still fail exactly as in the round before, the rounds stop (<see cref="ImplementStep.NoProgress"/>).
    /// </summary>
    public static ImplementStep Decide(ImplementProgress progress)
    {
        var (checksOk, prBodyProblems, fixRoundsUsed, maxFixRounds, canRunAgain) = progress;

        return checksOk && prBodyProblems == 0 ? ImplementStep.Done
            : !canRunAgain || fixRoundsUsed >= maxFixRounds ? ImplementStep.OutOfRounds
            : checksOk && fixRoundsUsed >= 1 ? ImplementStep.OutOfRounds
            : fixRoundsUsed >= 1 && progress.IsSameAsLastRound ? ImplementStep.NoProgress
            : ImplementStep.Fix;
    }

    /// <summary>
    /// A fix round runs Copilot and the checks again, each up to the per-call timeout. Starting one with
    /// less time left would let the job timeout cancel it mid-way, and a cancelled job saves nothing.
    /// </summary>
    public static bool TimeForAnotherRound(TimeSpan left, TimeSpan perCall) => left >= perCall * 2;

    /// <summary>
    /// What a round ended with: each failing check (command, status, output) and each output-file problem. Times and
    /// durations are blanked out, since they change on every run; everything else counts, so a changed error, line
    /// number or count of failing tests is progress and the rounds go on.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<CheckFailure> failures, IReadOnlyList<string> outputProblems)
    {
        var builder = new StringBuilder();
        foreach (var failure in failures)
        {
            var withoutTimes = Timestamp.Replace(failure.Output, "#");
            var output = Duration.Replace(withoutTimes, "#");
            builder.AppendLine(failure.Command);
            builder.AppendLine(failure.Result);
            builder.AppendLine(output);
        }

        foreach (var problem in outputProblems)
        {
            builder.AppendLine(problem);
        }

        return builder.ToString();
    }
}
