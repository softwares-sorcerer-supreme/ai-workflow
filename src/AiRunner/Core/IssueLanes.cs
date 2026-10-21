namespace AiRunner.Core;

/// <summary>
/// The ten stages grouped into five lanes, left to right: the columns of the issue board and the steps of the
/// pipeline summary. A long list is easier to follow by "where is it" than by ten stage names.
/// </summary>
public static class IssueLane
{
    public const string ToDo = "todo";
    public const string Ready = "ready";
    public const string InProgress = "in-progress";
    public const string InReview = "in-review";
    public const string Finished = "finished";

    /// <summary>Every lane in board order, with its label.</summary>
    public static IReadOnlyList<CodeLabel> Lanes { get; } =
    [
        new(ToDo, "To do"),
        new(Ready, "Ready"),
        new(InProgress, "In progress"),
        new(InReview, "In review"),
        new(Finished, "Done"),
    ];

    /// <summary>
    /// The lane of a stage code. Merged belongs to Done: nothing is left for a person, the runner closes the issue on
    /// its next poll. An unknown code is To do, so a new stage never drops an issue from the board.
    /// </summary>
    public static CodeLabel Of(string stageCode)
    {
        var code = stageCode switch
        {
            IssueStage.ReadySimple or IssueStage.ReadyComplex => Ready,
            IssueStage.Implementing or IssueStage.BrokenDown => InProgress,
            IssueStage.PrOpen => InReview,
            IssueStage.Merged or IssueStage.Done or IssueStage.ClosedNoMerge => Finished,
            _ => ToDo,
        };

        return Lanes.First(lane => lane.Code == code);
    }
}

/// <summary>What the focus of one issue is decided from.</summary>
/// <param name="Waiting">The <see cref="WaitingOn"/> action of the issue, or null.</param>
public sealed record FocusInput(IssueSnapshot Issue, StageResult Stage, CodeLabel? Waiting);

/// <summary>
/// Quick views across lanes: what needs a person, what the runner is on, what failed. An issue can be in more than
/// one (a failed issue also needs a person). A focus is about work still to do, so a closed issue is in none.
/// </summary>
public static class IssueFocus
{
    public const string NeedsYou = "needs-you";
    public const string Working = "working";
    public const string Failed = "failed";

    /// <summary>Every focus in the order of the quick filters, with its label.</summary>
    public static IReadOnlyList<CodeLabel> Focuses { get; } =
    [
        new(NeedsYou, "Needs you"),
        new(Working, "Runner working"),
        new(Failed, "Failed"),
    ];

    public static bool Matches(string focus, FocusInput input)
    {
        if (!input.Issue.IsOpen)
        {
            return false;
        }

        return focus switch
        {
            NeedsYou => input.Waiting is not null,
            Working => input.Stage.Badge is not null,
            Failed => input.Stage.IsFailed,
            _ => false,
        };
    }
}
