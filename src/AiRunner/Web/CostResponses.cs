using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Web;

/// <summary>One group of the cost table. <c>JobsWithoutCount</c> are jobs whose Copilot calls are not known.</summary>
public sealed record CostRowResponse(
    string Key,
    string Label,
    int Calls,
    int Jobs,
    int JobsWithoutCount,
    double DurationSeconds)
{
    public static CostRowResponse From(CostRow row) =>
        new(row.Key, row.Label, row.Calls, row.Jobs, row.JobsWithoutCount, row.DurationSeconds);
}

/// <summary>
/// <c>GET /api/cost</c>: one row per day, project or issue (an issue includes its sub-issues), the total of the same
/// jobs, and <c>Note</c> when some jobs have no call count (older runs, failed jobs). <c>From</c> and <c>To</c> echo
/// the inclusive local days that were asked for.
/// </summary>
public sealed record CostResponse(
    string By,
    string ByLabel,
    string? From,
    string? To,
    IReadOnlyList<CostRowResponse> Rows,
    CostRowResponse Total,
    string? Note);

/// <summary>A cost reader for a runner that has no database yet.</summary>
public sealed class NoCost : ICostReader
{
    public static NoCost Instance { get; } = new();

    public IReadOnlyList<CostRecord> ListCost() => [];
}

/// <summary>Reads the cost log and the snapshot and asks <see cref="CostReport"/> for the totals.</summary>
public sealed class CostReader(RunnerConfig config, ICostReader cost, ISnapshotReader snapshot, TimeProvider time)
{
    public const string DateFormat = "yyyy-MM-dd";

    public CostResponse Read(CostQuery query)
    {
        // Only the issue grouping needs the snapshot; the dashboard polls this route, so day and project skip it.
        var needsSnapshot = query.By == CostGrouping.Issue;
        IReadOnlyList<IssueSnapshot> issues = needsSnapshot ? snapshot.ListIssues() : [];
        IReadOnlyList<PullSnapshot> pulls = needsSnapshot ? snapshot.ListPulls() : [];
        var input = new CostInput(cost.ListCost(), issues, pulls, config, time.LocalTimeZone);
        var result = CostReport.Build(input, query);
        var rows = result.Rows
            .Select(CostRowResponse.From)
            .ToList();

        return new CostResponse(
            CodeOf(query.By),
            LabelOf(query.By),
            query.Range.From?.ToString(DateFormat),
            query.Range.To?.ToString(DateFormat),
            rows,
            CostRowResponse.From(result.Total),
            NoteOf(result.Total));
    }

    public static string CodeOf(CostGrouping by) => by.ToString().ToLowerInvariant();

    private static string LabelOf(CostGrouping by) => by switch
    {
        CostGrouping.Project => "By project",
        CostGrouping.Issue => "By issue",
        _ => "By day",
    };

    private static string? NoteOf(CostRow total) => total.JobsWithoutCount switch
    {
        0 => null,
        1 => "1 job has no call count (an older run or a failed job), so the calls may be higher.",
        var count => $"{count} jobs have no call count (older runs or failed jobs), so the calls may be higher.",
    };
}
