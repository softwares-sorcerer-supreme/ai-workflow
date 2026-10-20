using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;

namespace AiRunner.Web;

/// <summary>A value computed in C#: a stable code for the UI to style plus the text to show.</summary>
public sealed record CodeLabelResponse(string Code, string Label)
{
    public static CodeLabelResponse From(CodeLabel value) => new(value.Code, value.Label);
}

/// <summary>
/// One issue of the snapshot with its stage, flags and badge. <c>SubIssues</c> holds the sub-issues of a parent in
/// the nested list and is empty in the flat list.
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
    IReadOnlyList<CodeLabelResponse> Flags,
    CodeLabelResponse? Badge,
    string? Component,
    string? HtmlUrl,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    int? Parent,
    IReadOnlyList<IssueRowResponse> SubIssues);

/// <summary>A project and its code components: the options of the project and component filters.</summary>
public sealed record ProjectOptionResponse(string Name, IReadOnlyList<string> Components);

/// <summary><c>GET /api/issues</c>: the issue rows, newest first, and the options of the filters.</summary>
public sealed record IssueListResponse(
    IReadOnlyList<IssueRowResponse> Items,
    IReadOnlyList<ProjectOptionResponse> Projects,
    IReadOnlyList<CodeLabelResponse> Stages);

/// <summary>The filters of <c>GET /api/issues</c>, already checked. Null = no filter.</summary>
public sealed record IssueQuery(string? Project, string? Component, string? Stage, string? State, bool IsFlat);

/// <summary>
/// Builds the issue list from the snapshot, the job table and the running job. The stage, flags and badge come from
/// <see cref="IssueStage"/>; this class only reads, filters and nests.
/// </summary>
public sealed class IssueReader(RunnerConfig config, IssueBoardReader boardReader)
{
    public IssueListResponse Read(IssueQuery query)
    {
        var board = boardReader.Read(query.Project);
        var comparer = IssueBoard.KeyComparer;
        var rows = board.Entries.ToDictionary(
            entry => entry.Key,
            entry => RowOf(entry.Value),
            comparer);
        var subIssuesOf = board.Entries.Values
            .SelectMany(entry => entry.SubIssues)
            .ToLookup(subIssue => IssueBoard.KeyOf(subIssue.Repo, subIssue.Parent!.Value), comparer);

        var matching = rows
            .Where(entry => Matches(entry.Value, query))
            .Select(entry => entry.Key)
            .ToHashSet(comparer);
        var items = query.IsFlat
            ? Newest(matching.Select(key => rows[key]))
            : Nest(rows, matching, subIssuesOf);
        var projectOptions = config.Projects
            .Select(project => new ProjectOptionResponse(project.Name, project.CodeComponentNames))
            .ToList();
        var stageOptions = IssueStage.Stages
            .Select(CodeLabelResponse.From)
            .ToList();

        return new IssueListResponse(items, projectOptions, stageOptions);
    }

    /// <summary>The list row of one entry, without sub-issues.</summary>
    public static IssueRowResponse RowOf(IssueEntry entry)
    {
        var component = SnapshotRules.ComponentOf(entry.Issue, entry.Project);

        return Row(entry.Issue, entry.Stage, component);
    }

    private static IssueRowResponse Row(IssueSnapshot issue, StageResult stage, string? component)
    {
        var flags = stage.Flags
            .Select(CodeLabelResponse.From)
            .ToList();
        var badge = stage.Badge is { } value ? CodeLabelResponse.From(value) : null;
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
            flags,
            badge,
            component,
            issue.HtmlUrl,
            issue.UpdatedAt,
            issue.ClosedAt,
            issue.Parent,
            []);
    }

    private static bool Matches(IssueRowResponse row, IssueQuery query) =>
        (query.Project is null || row.Project == query.Project) &&
        (query.Component is null || row.Component == query.Component) &&
        (query.Stage is null || row.Stage == query.Stage) &&
        (query.State is null || row.State == query.State);

    private static List<IssueRowResponse> Newest(IEnumerable<IssueRowResponse> rows) =>
        rows
            .OrderByDescending(row => row.Number)
            .ThenBy(row => row.Repo, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

        return Newest(items);
    }

}
