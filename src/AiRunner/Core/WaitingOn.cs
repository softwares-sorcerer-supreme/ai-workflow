namespace AiRunner.Core;

/// <summary>A sub-issue as <see cref="WaitingOn"/> needs it: open or closed, its stage code, and whether a job is on it.</summary>
public sealed record SubIssueProgress(bool IsOpen, string StageCode, bool IsBusy);

/// <summary>What the human action of one issue is computed from.</summary>
/// <param name="Stage">The result of <see cref="IssueStage.Compute"/> for the issue (stage, flags, badge, PRs).</param>
/// <param name="AutoStartFirstSubIssue">The project's setting: the runner starts the first sub-issue itself.</param>
/// <param name="SubIssues">The issue's sub-issues in build order (empty for any other issue).</param>
public sealed record WaitingInput(
    IssueSnapshot Issue,
    StageResult Stage,
    bool AutoStartFirstSubIssue,
    IReadOnlyList<SubIssueProgress> SubIssues);

/// <summary>
/// What a human must do next on an issue (docs/specs/dashboard.md, Stage), or nothing. Pure rules on the result of
/// <see cref="IssueStage"/>, so the UI only shows the text. The first match wins; an issue the runner is working on
/// (queued or running) and a closed issue wait on nobody.
/// </summary>
public static class WaitingOn
{
    public const string ReadFailure = "read-failure";
    public const string AnswerQuestions = "answer-questions";
    public const string AddImplement = "add-ai-implement";
    public const string AddBreakdown = "add-ai-breakdown";
    public const string ReviewAndMerge = "review-and-merge";
    public const string StartNextSubIssue = "start-next-sub-issue";

    public static CodeLabel? Of(WaitingInput input)
    {
        var (issue, stage, autoStartFirstSubIssue, subIssues) = input;

        if (!issue.IsOpen || stage.Badge is not null)
        {
            return null;
        }

        if (stage.Flags.Any(flag => flag.Code == IssueMarks.Failed))
        {
            return new CodeLabel(ReadFailure, "Read the failure, then remove ai:failed");
        }

        switch (stage.Stage.Code)
        {
            case IssueStage.QuestionsOpen:
                return new CodeLabel(AnswerQuestions, "Answer the questions with @ai");

            case IssueStage.ReadySimple when !issue.HasLabel(Labels.Implement):
                return new CodeLabel(AddImplement, $"Add {Labels.Implement}");

            case IssueStage.ReadyComplex when !issue.HasLabel(Labels.Breakdown):
                return new CodeLabel(AddBreakdown, $"Add {Labels.Breakdown}");

            case IssueStage.PrOpen:
                return ReviewAndMergeOf(stage);

            case IssueStage.BrokenDown when !autoStartFirstSubIssue && IsNextSubIssueWaiting(subIssues):
                return new CodeLabel(StartNextSubIssue, "Start the next sub-issue");

            default:
                return null;
        }
    }

    private static CodeLabel? ReviewAndMergeOf(StageResult stage)
    {
        var open = stage.Pulls.FirstOrDefault(pull => pull.IsOpen());

        // The stage is "PR open" only when a PR is open, so a missing one means the input is inconsistent.
        return open is null ? null : new CodeLabel(ReviewAndMerge, $"Review and merge PR #{open.Number}");
    }

    /// <summary>
    /// Nothing is in progress (no sub-issue implementing, in a PR, or with a job) and one is still open, so the
    /// runner will not start it by itself.
    /// </summary>
    private static bool IsNextSubIssueWaiting(IReadOnlyList<SubIssueProgress> subIssues)
    {
        var isInProgress = subIssues.Any(subIssue =>
            subIssue.IsBusy ||
            (subIssue.IsOpen && subIssue.StageCode is IssueStage.Implementing or IssueStage.PrOpen));

        return !isInProgress && subIssues.Any(subIssue => subIssue.IsOpen);
    }
}
