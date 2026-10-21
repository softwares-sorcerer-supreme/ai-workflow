using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Web;

/// <summary>A value computed in C#: a stable code for the UI to style plus the text to show.</summary>
public sealed record CodeLabelResponse(string Code, string Label)
{
    public static CodeLabelResponse From(CodeLabel value) => new(value.Code, value.Label);
}

/// <summary>A lane or a focus with the number of issues in it, for the summary above the list.</summary>
public sealed record CountResponse(string Code, string Label, int Count);

/// <summary>How far the sub-issues of a parent are: <c>Closed</c> of <c>Total</c> (the n/m of its stage label).</summary>
public sealed record SubIssueCountResponse(int Closed, int Total);

/// <summary>
/// One issue of the snapshot with its stage, lane, flags, badge and what it waits on. <c>SubIssues</c> holds the
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
    string LaneLabel,
    IReadOnlyList<CodeLabelResponse> Flags,
    CodeLabelResponse? Badge,
    CodeLabelResponse? Waiting,
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
/// <c>GET /api/issues</c>: the issue rows and the options of the filters. The counts take every issue (sub-issues
/// too) that matches the project, component, state and search filters, so the summary shows what each choice would
/// give: <c>Total</c> and <c>Focuses</c> whatever lane, stage or focus is chosen, <c>Lanes</c> under the chosen focus
/// (a board column then says how many cards it shows).
/// </summary>
public sealed record IssueListResponse(
    IReadOnlyList<IssueRowResponse> Items,
    IReadOnlyList<ProjectOptionResponse> Projects,
    IReadOnlyList<CodeLabelResponse> Stages,
    int Total,
    IReadOnlyList<CountResponse> Lanes,
    IReadOnlyList<CountResponse> Focuses);

/// <summary>
/// The filters of <c>GET /api/issues</c>, already checked. Null = no filter. <c>Search</c> matches the title (any
/// case) or the number ("12" or "#12"). <c>IsByUpdate</c> sorts by last update instead of by number.
/// </summary>
public sealed record IssueQuery(string? Project, string? Component, string? Stage, string? State, bool IsFlat)
{
    public string? Lane { get; init; }

    public string? Focus { get; init; }

    public string? Search { get; init; }

    public bool IsByUpdate { get; init; }
}

/// <summary>
/// Builds the issue list from the snapshot, the job table and the running job. The stage, lane, flags, badge and
/// action come from Core (<see cref="IssueStage"/>, <see cref="IssueLane"/>, <see cref="WaitingOn"/>); this class
/// only reads, filters, counts and nests.
/// </summary>
public sealed class IssueReader(RunnerConfig config, IssueBoardReader boardReader)
{
    public const string SortByNumber = "number";
    public const string SortByUpdate = "updated";

    public IssueListResponse Read(IssueQuery query)
    {
        var board = boardReader.Read(query.Project);
        var comparer = IssueBoard.KeyComparer;
        var rows = board.Entries.ToDictionary(
            entry => entry.Key,
            entry => RowOf(entry.Value, board),
            comparer);
        var subIssuesOf = board.Entries.Values
            .SelectMany(entry => entry.SubIssues)
            .ToLookup(subIssue => IssueBoard.KeyOf(subIssue.Repo, subIssue.Parent!.Value), comparer);

        var inScope = board.Entries.Values
            .Where(entry => IsInScope(rows[entry.Issue.Key], query))
            .ToList();
        var matching = inScope
            .Where(entry => IsChosen(entry, query))
            .Select(entry => entry.Issue.Key)
            .ToHashSet(comparer);
        var items = query.IsFlat
            ? Sorted(matching.Select(key => rows[key]), query)
            : Sorted(Nest(rows, matching, subIssuesOf), query);

        var projectOptions = config.Projects
            .Select(project => new ProjectOptionResponse(project.Name, project.CodeComponentNames))
            .ToList();
        var stageOptions = IssueStage.Stages
            .Select(CodeLabelResponse.From)
            .ToList();
        var inFocus = inScope
            .Where(entry => query.Focus is null || IsInFocus(entry, query.Focus))
            .ToList();
        var laneCounts = IssueLane.Lanes
            .Select(lane => CountOf(lane, inFocus.Count(entry => IssueLane.Of(entry.Stage.Stage.Code) == lane)))
            .ToList();
        var focusCounts = IssueFocus.Focuses
            .Select(focus => CountOf(focus, inScope.Count(entry => IsInFocus(entry, focus.Code))))
            .ToList();

        return new IssueListResponse(items, projectOptions, stageOptions, inScope.Count, laneCounts, focusCounts);
    }

    /// <summary>The list row of one entry, without sub-issues.</summary>
    public static IssueRowResponse RowOf(IssueEntry entry, IssueBoard board)
    {
        var issue = entry.Issue;
        var stage = entry.Stage;
        var component = SnapshotRules.ComponentOf(issue, entry.Project);
        var lane = IssueLane.Of(stage.Stage.Code);
        var flags = stage.Flags
            .Select(CodeLabelResponse.From)
            .ToList();
        var badge = stage.Badge is { } value ? CodeLabelResponse.From(value) : null;
        var waiting = entry.Waiting is { } action ? CodeLabelResponse.From(action) : null;
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
            lane.Label,
            flags,
            badge,
            waiting,
            progress,
            pulls,
            component,
            issue.HtmlUrl,
            issue.UpdatedAt,
            issue.ClosedAt,
            issue.Parent,
            []);
    }

    /// <summary>Closed of all sub-issues, the same count the stage label "Broken down (n/m)" shows.</summary>
    private static SubIssueCountResponse? ProgressOf(IssueEntry entry)
    {
        if (entry.SubIssues.Count == 0)
        {
            return null;
        }

        var closedCount = entry.SubIssues.Count(subIssue => !subIssue.IsOpen);

        return new SubIssueCountResponse(closedCount, entry.SubIssues.Count);
    }

    private static CountResponse CountOf(CodeLabel value, int count) => new(value.Code, value.Label, count);

    /// <summary>The filters the summary counts are taken under: project, component, state and search.</summary>
    private static bool IsInScope(IssueRowResponse row, IssueQuery query) =>
        (query.Project is null || row.Project == query.Project) &&
        (query.Component is null || row.Component == query.Component) &&
        (query.State is null || row.State == query.State) &&
        (query.Search is null || IsFound(row, query.Search));

    /// <summary>The choices the summary counts: stage, lane and focus.</summary>
    private static bool IsChosen(IssueEntry entry, IssueQuery query) =>
        (query.Stage is null || entry.Stage.Stage.Code == query.Stage) &&
        (query.Lane is null || IssueLane.Of(entry.Stage.Stage.Code).Code == query.Lane) &&
        (query.Focus is null || IsInFocus(entry, query.Focus));

    /// <summary>A focus is about work still to do, so a closed issue is in none.</summary>
    private static bool IsInFocus(IssueEntry entry, string focus) =>
        entry.Issue.IsOpen && IssueFocus.Matches(focus, entry.Stage, entry.Waiting);

    private static bool IsFound(IssueRowResponse row, string search)
    {
        var text = search.Trim();
        var number = text.TrimStart('#');

        if (int.TryParse(number, out var wanted) && wanted == row.Number)
        {
            return true;
        }

        return row.Title.Contains(text, StringComparison.OrdinalIgnoreCase);
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
    /// filters, with the matching sub-issues only. A sub-issue whose parent is not in the snapshot stands on its own.
    /// </summary>
    private static List<IssueRowResponse> Nest(
        IReadOnlyDictionary<string, IssueRowResponse> rows,
        IReadOnlySet<string> matching,
        ILookup<string, IssueSnapshot> subIssuesOf)
    {
        var items = new List<IssueRowResponse>();
        foreach (var (key, row) in rows)
        {
            // A sub-issue of a parent that is shown goes under that parent instead.
            if (row.Parent is { } parent && rows.ContainsKey(IssueBoard.KeyOf(row.Repo, parent)))
            {
                continue;
            }

            var subIssues = subIssuesOf[key]
                .Where(subIssue => matching.Contains(subIssue.Key))
                .Select(subIssue => rows[subIssue.Key])
                .ToList();

            if (subIssues.Count == 0 && !matching.Contains(key))
            {
                continue;
            }

            items.Add(row with
            {
                SubIssues = subIssues
            });
        }

        return items;
    }
}
