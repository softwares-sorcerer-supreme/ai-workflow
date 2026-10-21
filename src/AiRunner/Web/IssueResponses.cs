using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Web;

/// <summary>A value computed in C#: a stable code for the UI to style plus the text to show.</summary>
public sealed record CodeLabelResponse(string Code, string Label)
{
    public static CodeLabelResponse From(CodeLabel value) => new(value.Code, value.Label);
}

/// <summary>
/// One action the user can take now (<see cref="IssueActions"/>): the code to send, the button text, and whether the
/// user types a text for it. The UI shows exactly the actions it gets.
/// </summary>
public sealed record ActionResponse(string Code, string Label, bool NeedsText)
{
    public static ActionResponse From(IssueAction action) => new(action.Code, action.Label, action.NeedsText);

    public static List<ActionResponse> ListOf(IEnumerable<IssueAction> actions) =>
        actions
            .Select(From)
            .ToList();
}

/// <summary>A lane or a focus with the number of issues in it, for the summary above the list.</summary>
public sealed record CountResponse(string Code, string Label, int Count);

/// <summary>How far the sub-issues of a parent are: <c>Closed</c> of <c>Total</c> (the n/m of its stage).</summary>
public sealed record SubIssueCountResponse(int Closed, int Total);

/// <summary>
/// One issue of the snapshot with its stage, lane (a code of <c>IssueListResponse.Lanes</c>), flags, badge and what
/// it waits on. <c>SubIssues</c> holds the
/// sub-issues of a parent in the nested list and is empty in the flat list. <c>Progress</c> is set for a parent only.
/// <c>Pulls</c> are its PRs (found by branch, or linking the issue), newest first.
/// </summary>
public sealed record IssueRowResponse(
    string Project,
    string Repo,
    int Number,
    string Title,
    string State,
    string StateLabel,
    string Stage,
    string StageLabel,
    string Lane,
    IReadOnlyList<CodeLabelResponse> Flags,
    CodeLabelResponse? Badge,
    CodeLabelResponse? Waiting,
    IReadOnlyList<ActionResponse> Actions,
    SubIssueCountResponse? Progress,
    IReadOnlyList<PullResponse> Pulls,
    string? Component,
    string? HtmlUrl,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    int? Parent,
    IReadOnlyList<IssueRowResponse> SubIssues);

/// <summary>A project and its code components: the options of the project and component filters.</summary>
public sealed record ProjectOptionResponse(string Name, IReadOnlyList<string> Components);

/// <summary>
/// <c>GET /api/issues</c>: the issue rows and the options of the filters (<c>States</c> and <c>Sorts</c> are the values
/// <c>state</c> and <c>sort</c> accept). The counts take every issue (sub-issues
/// too) that matches the project, component, state and search filters, so the summary shows what each choice would
/// give: <c>Total</c> and <c>Focuses</c> whatever lane, stage or focus is chosen, <c>Lanes</c> under the chosen focus
/// (a lane on the board then says how many cards it shows).
/// </summary>
public sealed record IssueListResponse(
    IReadOnlyList<IssueRowResponse> Items,
    IReadOnlyList<ProjectOptionResponse> Projects,
    IReadOnlyList<CodeLabelResponse> Stages,
    IReadOnlyList<CodeLabelResponse> States,
    IReadOnlyList<CodeLabelResponse> Sorts,
    int Total,
    IReadOnlyList<CountResponse> Lanes,
    IReadOnlyList<CountResponse> Focuses);

/// <summary>
/// The filters of <c>GET /api/issues</c>, already checked. Null = no filter. <c>Search</c> (trimmed, never blank)
/// matches the title (any case) or the number ("12" or "#12"). <c>IsByUpdate</c> sorts by last update instead of by
/// number.
/// </summary>
public sealed record IssueQuery(
    string? Project,
    string? Component,
    string? Stage,
    string? State,
    string? Lane,
    string? Focus,
    string? Search,
    bool IsFlat,
    bool IsByUpdate);

/// <summary>
/// Builds the issue list from the snapshot, the job table and the running job. The stage, lane, flags, badge and
/// action come from Core (<see cref="IssueStage"/>, <see cref="IssueLane"/>, <see cref="WaitingOn"/>); this class
/// only reads, filters, counts and nests.
/// </summary>
public sealed class IssueReader(RunnerConfig config, IssueBoardReader boardReader)
{
    public const string SortByNumber = "number";
    public const string SortByUpdate = "updated";

    /// <summary>The <c>state</c> that keeps open and closed issues (the same as none).</summary>
    public const string AllStates = "all";

    public static IReadOnlyList<CodeLabel> States { get; } =
    [
        new(SnapshotRules.Open, "Open"),
        new(SnapshotRules.Closed, "Closed"),
        new(AllStates, "All"),
    ];

    public static IReadOnlyList<CodeLabel> Sorts { get; } =
    [
        new(SortByNumber, "Newest"),
        new(SortByUpdate, "Recently updated"),
    ];

    public IssueListResponse Read(IssueQuery query)
    {
        var board = boardReader.Read(query.Project);

        var inScope = board.Entries.Values
            .Where(entry => IsInScope(entry, query))
            .ToList();
        var matching = inScope
            .Where(entry => IsChosen(entry, query))
            .Select(entry => entry.Issue.Key)
            .ToHashSet(IssueBoard.KeyComparer);
        List<IssueRowResponse> shown;
        if (query.IsFlat)
        {
            shown = matching
                .Select(key => RowOf(board.Entries[key], board))
                .ToList();
        }
        else
        {
            shown = Nest(board, matching);
        }

        var items = Sorted(shown, query);

        var projectOptions = config.Projects
            .Select(project => new ProjectOptionResponse(project.Name, project.CodeComponentNames))
            .ToList();
        var inFocus = inScope
            .Where(entry => query.Focus is null || IsInFocus(entry, query.Focus))
            .ToList();
        var laneCounts = IssueLane.Lanes
            .Select(lane => CountOf(lane, inFocus.Count(entry => LaneOf(entry) == lane)))
            .ToList();
        var focusCounts = IssueFocus.Focuses
            .Select(focus => CountOf(focus, inScope.Count(entry => IsInFocus(entry, focus.Code))))
            .ToList();

        return new IssueListResponse(
            items,
            projectOptions,
            Options(IssueStage.Stages),
            Options(States),
            Options(Sorts),
            inScope.Count,
            laneCounts,
            focusCounts);
    }

    private static List<CodeLabelResponse> Options(IReadOnlyList<CodeLabel> values) =>
        values
            .Select(CodeLabelResponse.From)
            .ToList();

    /// <summary>The list row of one entry, without sub-issues.</summary>
    public static IssueRowResponse RowOf(IssueEntry entry, IssueBoard board)
    {
        var issue = entry.Issue;
        var stage = entry.Stage;
        var component = SnapshotRules.ComponentOf(issue, entry.Project);
        var lane = LaneOf(entry);
        var flags = stage.Flags
            .Select(CodeLabelResponse.From)
            .ToList();
        var badge = stage.Badge is { } value ? CodeLabelResponse.From(value) : null;
        var waiting = entry.Waiting is { } action ? CodeLabelResponse.From(action) : null;
        var actions = ActionResponse.ListOf(entry.Actions);
        var progress = ProgressOf(entry);
        var pulls = board.PullsOf(entry);
        var stateLabel = SnapshotRules.StateLabel(issue.State);

        return new IssueRowResponse(
            issue.Project,
            issue.Repo,
            issue.Number,
            issue.Title,
            issue.State,
            stateLabel,
            stage.Stage.Code,
            stage.Stage.Label,
            lane.Code,
            flags,
            badge,
            waiting,
            actions,
            progress,
            pulls,
            component,
            issue.HtmlUrl,
            issue.UpdatedAt,
            issue.ClosedAt,
            issue.Parent,
            []);
    }

    /// <summary>The count the stage label "Broken down (n/m)" shows; null for an issue without sub-issues.</summary>
    private static SubIssueCountResponse? ProgressOf(IssueEntry entry)
    {
        if (entry.SubIssues.Count == 0)
        {
            return null;
        }

        var count = SubIssueCount.Of(entry.SubIssues);

        return new SubIssueCountResponse(count.Closed, count.Total);
    }

    private static CountResponse CountOf(CodeLabel value, int count) => new(value.Code, value.Label, count);

    private static CodeLabel LaneOf(IssueEntry entry) => IssueLane.Of(entry.Stage.Stage.Code);

    /// <summary>The filters the summary counts are taken under: project, component, state and search.</summary>
    private static bool IsInScope(IssueEntry entry, IssueQuery query)
    {
        var issue = entry.Issue;

        return (query.Project is null || issue.Project == query.Project) &&
            (query.Component is null || SnapshotRules.ComponentOf(issue, entry.Project) == query.Component) &&
            (query.State is null || issue.State == query.State) &&
            (query.Search is null || IsFound(issue, query.Search));
    }

    /// <summary>The choices the summary counts: stage, lane and focus.</summary>
    private static bool IsChosen(IssueEntry entry, IssueQuery query) =>
        (query.Stage is null || entry.Stage.Stage.Code == query.Stage) &&
        (query.Lane is null || LaneOf(entry).Code == query.Lane) &&
        (query.Focus is null || IsInFocus(entry, query.Focus));

    private static bool IsInFocus(IssueEntry entry, string focus) =>
        IssueFocus.Matches(focus, new FocusInput(entry.Issue, entry.Stage, entry.Waiting));

    private static bool IsFound(IssueSnapshot issue, string search)
    {
        var number = search.TrimStart('#');

        if (int.TryParse(number, out var wanted) && wanted == issue.Number)
        {
            return true;
        }

        return issue.Title.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Newest number first, or most recently updated first; the repo breaks a tie.</summary>
    private static List<IssueRowResponse> Sorted(IEnumerable<IssueRowResponse> rows, IssueQuery query)
    {
        var ordered = query.IsByUpdate
            ? rows.OrderByDescending(row => row.UpdatedAt)
            : rows.OrderByDescending(row => row.Number);

        return ordered
            .ThenBy(row => row.Repo, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Sub-issues go under their parent, in build order. A parent shows when it or one of its sub-issues matches the
    /// filters, with the matching sub-issues only. A sub-issue whose parent is not on the board stands on its own.
    /// Rows are built only for what is shown.
    /// </summary>
    private static List<IssueRowResponse> Nest(IssueBoard board, IReadOnlySet<string> matching)
    {
        var items = new List<IssueRowResponse>();
        foreach (var (key, entry) in board.Entries)
        {
            var issue = entry.Issue;

            // A sub-issue of a parent that is shown goes under that parent instead.
            if (issue.Parent is { } parent && board.Entries.ContainsKey(IssueBoard.KeyOf(issue.Repo, parent)))
            {
                continue;
            }

            var subIssues = entry.SubIssues
                .Where(subIssue => matching.Contains(subIssue.Key))
                .Select(subIssue => RowOf(board.Entries[subIssue.Key], board))
                .ToList();

            if (subIssues.Count == 0 && !matching.Contains(key))
            {
                continue;
            }

            var row = RowOf(entry, board);
            items.Add(row with
            {
                SubIssues = subIssues
            });
        }

        return items;
    }
}
