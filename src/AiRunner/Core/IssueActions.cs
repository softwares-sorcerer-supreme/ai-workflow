using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>One thing a person can ask the runner to do from the dashboard: a code, its button text, and whether the user types a text.</summary>
public sealed record IssueAction(string Code, string Label, bool NeedsText);

/// <summary>The codes of the actions. The UI keys its text box and icons on them, so they never change.</summary>
public static class ActionCodes
{
    public const string Implement = "implement";
    public const string Breakdown = "breakdown";
    public const string Retry = "retry";
    public const string Answer = "answer";
    public const string Ask = "ask";
    public const string StartNextSubIssue = "start-next-sub-issue";
    public const string ReviewFull = "review-full";
    public const string Fix = "fix";
}

/// <summary>What the actions of one issue are computed from: the inputs of <see cref="WaitingOn"/> plus the project's settings.</summary>
/// <param name="SubIssues">The issue's sub-issues in build order, parallel to <c>Waiting.SubIssues</c>.</param>
public sealed record IssueActionInput(
    WaitingInput Waiting,
    ProjectSettings Settings,
    IReadOnlyList<IssueSnapshot> SubIssues);

/// <summary>What the actions of one PR are computed from.</summary>
/// <param name="BotLogin">The runner's login on the code host: its own PRs are in scope by it.</param>
/// <param name="HasActiveJob">The runner has a job queued or running for the PR.</param>
public sealed record PullActionInput(
    HostPull Pull,
    ProjectSettings Settings,
    string BotLogin,
    bool HasActiveJob);

/// <summary>The kinds of write an action makes on the code host.</summary>
public enum ActionWriteKind
{
    AddLabel,
    RemoveLabel,
    Comment,
}

/// <summary>One write on the code host. <c>Value</c> is the label name or the comment text.</summary>
public sealed record ActionWrite(ActionWriteKind Kind, IssueRef Target, string Value);

/// <summary>What <see cref="IssueActions.WriteOf"/> needs to describe the write of an action.</summary>
/// <param name="Target">The issue or PR to write on (for the next sub-issue: that sub-issue).</param>
/// <param name="Text">What the user typed; required for the actions that need a text.</param>
public sealed record ActionWriteContext(IssueRef Target, string? Text, ProjectSettings Settings, string RunnerName);

/// <summary>
/// The actions a person can take on an issue or a PR from the dashboard (docs/specs/dashboard-control.md section 4).
/// Pure rules on the same inputs as <see cref="WaitingOn"/>, so the list row, the detail and the check right before a
/// write all agree, and the UI only shows the buttons it gets. Every action is a code-host write the planner then
/// picks up; none puts a job in the queue.
/// </summary>
public static class IssueActions
{
    private const string TraceLine = "_(sent from the ai-runner dashboard on {0})_";

    private static readonly IReadOnlyDictionary<string, IssueAction> Known = new[]
    {
        new IssueAction(ActionCodes.Implement, "Implement", false),
        new IssueAction(ActionCodes.Breakdown, "Break down", false),
        new IssueAction(ActionCodes.Retry, "Retry", false),
        new IssueAction(ActionCodes.Answer, "Answer", true),
        new IssueAction(ActionCodes.Ask, "Ask", true),
        new IssueAction(ActionCodes.StartNextSubIssue, "Start next sub-issue", false),
        new IssueAction(ActionCodes.ReviewFull, "Full review", false),
        new IssueAction(ActionCodes.Fix, "Ask for a fix", true),
    }.ToDictionary(action => action.Code);

    /// <summary>The action with this code, or null when there is no such action.</summary>
    public static IssueAction? Find(string code) => Known.GetValueOrDefault(code);

    /// <summary>The actions of an issue that are offered now, in the order the buttons are shown.</summary>
    public static IReadOnlyList<IssueAction> Available(IssueActionInput input)
    {
        var (waiting, settings, _) = input;
        var (issue, stage, _, _) = waiting;
        var actions = new List<IssueAction>();

        // An issue that is closed, queued or running waits on nobody, and the planner would skip a failed one.
        if (!issue.IsOpen || stage.Badge is not null)
        {
            return actions;
        }

        if (stage.IsFailed)
        {
            return [Known[ActionCodes.Retry]];
        }

        // Cloud fallback issues are skipped by the planner: a label or a comment would do nothing.
        if (IsCloudFallback(issue))
        {
            return actions;
        }

        var waitingOn = WaitingOn.Of(waiting)?.Code;

        if (waitingOn == WaitingOn.AddImplement && settings.IsEnabled(Workflows.Implement))
        {
            actions.Add(Known[ActionCodes.Implement]);
        }

        if (waitingOn == WaitingOn.AddBreakdown && settings.IsEnabled(Workflows.Breakdown))
        {
            actions.Add(Known[ActionCodes.Breakdown]);
        }

        if (waitingOn == WaitingOn.StartNextSubIssue && NextSubIssueToStart(input) is not null)
        {
            actions.Add(Known[ActionCodes.StartNextSubIssue]);
        }

        if (settings.IsEnabled(Workflows.Analyze))
        {
            AddAnalysisAction(actions, stage, issue);
        }

        return actions;
    }

    /// <summary>
    /// The sub-issue "start the next sub-issue" puts <c>ai-implement</c> on: the first open one in build order. Null
    /// when the issue has none to start, the implement workflow is off, or that sub-issue failed (Retry is its action).
    /// </summary>
    public static IssueSnapshot? NextSubIssueToStart(IssueActionInput input)
    {
        if (!input.Settings.IsEnabled(Workflows.Implement))
        {
            return null;
        }

        var next = input.SubIssues.FirstOrDefault(subIssue => subIssue.IsOpen);

        return next is null || next.HasLabel(Labels.Failed) ? null : next;
    }

    /// <summary>The actions of a PR that are offered now.</summary>
    public static IReadOnlyList<IssueAction> AvailableForPull(PullActionInput input)
    {
        var (pull, settings, botLogin, hasActiveJob) = input;
        var actions = new List<IssueAction>();

        var isUntouchable = !pull.IsOpen() ||
            pull.Draft == true ||
            hasActiveJob ||
            pull.Title.TrimStart().StartsWith(JobPlanner.SkipAiPrefix, StringComparison.OrdinalIgnoreCase);
        if (isUntouchable)
        {
            return actions;
        }

        var isInScope = JobPlanner.InScope(pull, botLogin, settings.TriggerLabel);

        // Same scope rule as the review: scoped reviews only PRs in scope, "all" reviews every open PR.
        var isReviewable = !settings.Review.IsScoped || isInScope;
        if (settings.IsEnabled(Workflows.Review) && isReviewable && !pull.LabelNames().Contains(Labels.ReviewFull))
        {
            actions.Add(Known[ActionCodes.ReviewFull]);
        }

        // The fix pushes to the PR's branch, so it is scoped whatever review.scope says.
        if (settings.IsEnabled(Workflows.Fix) && isInScope)
        {
            actions.Add(Known[ActionCodes.Fix]);
        }

        return actions;
    }

    /// <summary>
    /// The write of an action, or null when the action needs a text and has none (or the code is unknown). The comment
    /// is a people's comment: no bot marker, so the planner takes it as a trigger like one typed on the code host.
    /// </summary>
    public static ActionWrite? WriteOf(string code, ActionWriteContext context)
    {
        var (target, text, settings, runnerName) = context;

        switch (code)
        {
            case ActionCodes.Implement:
            case ActionCodes.StartNextSubIssue:
                return new ActionWrite(ActionWriteKind.AddLabel, target, Labels.Implement);

            case ActionCodes.Breakdown:
                return new ActionWrite(ActionWriteKind.AddLabel, target, Labels.Breakdown);

            case ActionCodes.ReviewFull:
                return new ActionWrite(ActionWriteKind.AddLabel, target, Labels.ReviewFull);

            case ActionCodes.Retry:
                return new ActionWrite(ActionWriteKind.RemoveLabel, target, Labels.Failed);

            case ActionCodes.Answer:
            case ActionCodes.Ask:
            case ActionCodes.Fix:
                return CommentWrite(context);

            default:
                return null;
        }
    }

    private static ActionWrite? CommentWrite(ActionWriteContext context)
    {
        var (target, text, settings, runnerName) = context;

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // The user's text can never fake a marker: the same neutralising as the runner's own blocks.
        var safeText = text.Trim().Replace("<!--", "&lt;!--", StringComparison.Ordinal);
        var trace = string.Format(TraceLine, runnerName);
        var body = $"{settings.TriggerKeyword} {safeText}\n\n{trace}";

        return new ActionWrite(ActionWriteKind.Comment, target, body);
    }

    private static void AddAnalysisAction(List<IssueAction> actions, StageResult stage, IssueSnapshot issue)
    {
        if (stage.Stage.Code == IssueStage.QuestionsOpen)
        {
            actions.Add(Known[ActionCodes.Answer]);
            return;
        }

        // The planner reaches the analysis only for an issue that is not an implement or breakdown request.
        var isAnalysisStage = stage.Stage.Code is IssueStage.New or IssueStage.ReadySimple or IssueStage.ReadyComplex;
        var hasRequest = issue.HasLabel(Labels.Implement) || issue.HasLabel(Labels.Breakdown);
        if (isAnalysisStage && !hasRequest)
        {
            actions.Add(Known[ActionCodes.Ask]);
        }
    }

    private static bool IsCloudFallback(IssueSnapshot issue) =>
        issue.HasLabel(Labels.OnGitHub) || issue.HasLabel(Labels.NeedsCloudAgent);
}
