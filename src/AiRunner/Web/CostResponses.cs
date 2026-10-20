using System.Globalization;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Web;

/// <summary>
/// One group of the cost table. <c>JobsWithoutCount</c> are jobs whose Copilot calls are not known,
/// <c>JobsWithoutUsage</c> jobs whose tokens and credits are not known. <c>NanoAiu</c> is what Copilot billed, as the
/// CLI reports it (totalNanoAiu); the four token counts are kept apart because each has its own price.
/// </summary>
public sealed record CostRowResponse(
    string Key,
    string Label,
    int Calls,
    int Jobs,
    int JobsWithoutCount,
    double DurationSeconds,
    long NanoAiu,
    long InputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    int JobsWithoutUsage)
{
    public static CostRowResponse From(CostRow row) =>
        new(
            row.Key,
            row.Label,
            row.Calls,
            row.Jobs,
            row.JobsWithoutCount,
            row.DurationSeconds,
            row.Usage.NanoAiu,
            row.Usage.InputTokens,
            row.Usage.CacheReadTokens,
            row.Usage.CacheWriteTokens,
            row.Usage.OutputTokens,
            row.JobsWithoutUsage);
}

/// <summary>
/// <c>GET /api/cost</c>: one row per day, project, issue (an issue includes its sub-issues) or model (per agent), the
/// total of the same jobs, and <c>Note</c> when some jobs have no call count or no usage (older runs, failed jobs).
/// <c>From</c> and <c>To</c> echo the inclusive local days that were asked for.
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

    public IReadOnlyList<CostRecord> ListCost(bool withParts = false) => [];
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
        var records = cost.ListCost(withParts: query.By == CostGrouping.Model);
        var input = new CostInput(records, issues, pulls, config, time.LocalTimeZone);
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
            NoteOf(result));
    }

    public static string CodeOf(CostGrouping by) => by.ToString().ToLowerInvariant();

    private static string LabelOf(CostGrouping by) => by switch
    {
        CostGrouping.Project => "By project",
        CostGrouping.Issue => "By issue",
        CostGrouping.Model => "By model and agent",
        _ => "By day",
    };

    /// <summary>What the totals leave out, or null when they are complete.</summary>
    private static string? NoteOf(CostResult result)
    {
        var total = result.Total;
        var calls = Sentence(
            total.JobsWithoutCount,
            "1 job has no call count (an older run or a failed job), so the calls may be higher.",
            "{0} jobs have no call count (older runs or failed jobs), so the calls may be higher.");
        var usage = Sentence(
            total.JobsWithoutUsage,
            "1 job has no usage (an older run or an older Copilot CLI), so billed and tokens may be higher.",
            "{0} jobs have no usage (older runs or an older Copilot CLI), so billed and tokens may be higher.");
        string?[] notes = [calls, usage];

        var outsideRows = Sentence(
            result.JobsOutsideRows,
            "1 job is in no row (an older run without numbers per model), so the rows sum to less than the total.",
            "{0} jobs are in no row (older runs without numbers per model), so the rows sum to less than the total.");
        var sharedJobs = result.RowsShareJobs
            ? "A job counts in each row it used, so jobs and job time add up to more than the total."
            : null;
        notes = [.. notes, outsideRows, sharedJobs];

        var present = notes.OfType<string>().ToList();

        return present.Count == 0 ? null : string.Join(" ", present);
    }

    /// <summary>
    /// A sentence about <paramref name="count"/> jobs: null for none, <paramref name="one"/> for one, else
    /// <paramref name="many"/> with the count in place of {0}.
    /// </summary>
    private static string? Sentence(int count, string one, string many) => count switch
    {
        0 => null,
        1 => one,
        _ => string.Format(CultureInfo.InvariantCulture, many, count),
    };
}
