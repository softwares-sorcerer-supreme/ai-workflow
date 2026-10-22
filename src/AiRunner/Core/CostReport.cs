using System.Globalization;
using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>
/// One row of the cost log. <c>CopilotCalls</c> is null for old rows and for failed jobs; <c>Usage</c> is null for old
/// rows, jobs without a Copilot call and a CLI without --usage-output-file (a failed job still has it).
/// </summary>
public sealed record CostRecord(
    DateTimeOffset At,
    string Repo,
    int Number,
    string Workflow,
    double DurationSeconds,
    int? CopilotCalls,
    CopilotUsage? Usage = null)
{
    /// <summary>
    /// What each agent spent on each model in each Copilot run of the job (empty for older rows, and unless the
    /// reader was asked for them).
    /// </summary>
    public IReadOnlyList<UsagePart> Parts { get; init; } = [];

    /// <summary>The model of the job's first Copilot run (null = Copilot's default, or an old row) and why.</summary>
    public string? Model { get; init; }

    public string? ModelReason { get; init; }

    /// <summary>The job ran Copilot (the count is unknown or above 0); a breakdown or routine job did not.</summary>
    public bool RanCopilot => CopilotCalls != 0;
}

public enum CostGrouping
{
    Day,
    Project,
    Issue,

    /// <summary>One row per model and agent ("claude-sonnet-5.5 - code-reviewer"), from the usage parts.</summary>
    Model,
}

/// <summary>Inclusive local days; null = open end.</summary>
public sealed record CostRange(DateOnly? From, DateOnly? To);

public sealed record CostQuery(CostGrouping By, CostRange Range);

/// <summary>Everything the grouping reads. <c>Zone</c> decides which day a job belongs to.</summary>
public sealed record CostInput(
    IReadOnlyList<CostRecord> Records,
    IReadOnlyList<IssueSnapshot> Issues,
    IReadOnlyList<PullSnapshot> Pulls,
    RunnerConfig Config,
    TimeZoneInfo Zone);

/// <summary>
/// One group. <c>Calls</c> adds only the jobs that know their count; <c>JobsWithoutCount</c> says how many did not,
/// so a total is never presented as complete when it is not. <c>Usage</c> and <c>JobsWithoutUsage</c> do the same
/// for the tokens and credits Copilot billed.
/// </summary>
public sealed record CostRow(
    string Key,
    string Label,
    int Calls,
    int Jobs,
    int JobsWithoutCount,
    double DurationSeconds)
{
    public CopilotUsage Usage { get; init; } = CopilotUsage.Zero;

    public int JobsWithoutUsage { get; init; }

    /// <summary>The models the group's jobs started with and why; rows from before model selection add nothing.</summary>
    public IReadOnlyList<ModelUse> Models { get; init; } = [];
}

/// <summary>How many jobs of a group used a model (null = Copilot's default) for a reason ("issue override").</summary>
public sealed record ModelUse(string? Model, string Reason, int Jobs);

/// <summary>
/// The rows and their total. A grouping that splits a job (model) sets the last two: jobs that are in no row, and
/// whether a job is in more than one row; the others leave them at 0 and false.
/// </summary>
public sealed record CostResult(IReadOnlyList<CostRow> Rows, CostRow Total)
{
    public int JobsOutsideRows { get; init; }

    public bool RowsShareJobs { get; init; }
}

/// <summary>
/// The cost totals of the dashboard (docs/specs/dashboard.md, API: cost). Pure: the rows are read elsewhere.
/// </summary>
public static class CostReport
{
    public const string OtherProject = "(other)";

    public static CostResult Build(CostInput input, CostQuery query)
    {
        var inRange = input.Records
            .Where(record => IsInRange(LocalDay(record, input.Zone), query.Range))
            .ToList();

        var groups = query.By switch
        {
            CostGrouping.Day => GroupBy(inRange, record => DayOf(record, input.Zone)),
            CostGrouping.Project => GroupBy(inRange, record => ProjectOf(record, input.Config)),
            CostGrouping.Model => ByModel(inRange),
            _ => GroupBy(inRange, new IssueGrouper(input).GroupOf),
        };

        // Billed credits first: that is what costs money; calls and time break ties (older rows have no usage).
        var rows = query.By == CostGrouping.Day
            ? groups.OrderBy(row => row.Key, StringComparer.Ordinal).ToList()
            : groups
                .OrderByDescending(row => row.Usage.NanoAiu)
                .ThenByDescending(row => row.Calls)
                .ThenByDescending(row => row.DurationSeconds)
                .ThenBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var total = RowOf("total", "Total", inRange);
        if (query.By != CostGrouping.Model)
        {
            return new CostResult(rows, total);
        }

        return new CostResult(rows, total)
        {
            JobsOutsideRows = inRange.Count(record => record.RanCopilot && record.Parts.Count == 0),
            RowsShareJobs = inRange.Any(IsInSeveralModelRows),
        };
    }

    /// <summary>
    /// One row per agent and model, from the usage parts. <c>Calls</c> counts the Copilot runs that agent made with
    /// that model, <c>Jobs</c> and the time are those of the jobs it appears in (a job with a subagent is in two rows).
    /// Jobs without parts (older rows) appear in no row, only in the total.
    /// </summary>
    private static List<CostRow> ByModel(IReadOnlyList<CostRecord> records)
    {
        // The index tells two equal-looking jobs apart, so each job counts once per row.
        return records
            .SelectMany((record, index) => record.Parts.Select(part => (Index: index, Record: record, Part: part)))
            .GroupBy(entry => (entry.Part.Model, entry.Part.Agent))
            .Select(group =>
            {
                var (model, agent) = group.Key;
                var name = $"{model} - {agent}";
                var jobs = group
                    .DistinctBy(entry => entry.Index)
                    .Select(entry => entry.Record)
                    .ToList();
                var partUsages = group.Select(entry => entry.Part.Usage);

                // Every part is a Copilot run whose call is known, so no job here lacks a count.
                return new CostRow(
                    name,
                    name,
                    group.Count(),
                    jobs.Count,
                    JobsWithoutCount: 0,
                    jobs.Sum(record => record.DurationSeconds))
                {
                    Usage = CopilotUsage.Sum(partUsages),
                };
            })
            .ToList();
    }

    private static List<CostRow> GroupBy(
        IReadOnlyList<CostRecord> records,
        Func<CostRecord, (string Key, string Label)> groupOf)
    {
        return records
            .Select(record => (Group: groupOf(record), Record: record))
            .GroupBy(entry => entry.Group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => RowOf(
                group.Key,
                group.First().Group.Label,
                group.Select(entry => entry.Record).ToList()))
            .ToList();
    }

    /// <summary>The job used more than one model and agent pair (e.g. a subagent), so it is in several rows.</summary>
    private static bool IsInSeveralModelRows(CostRecord record) =>
        record.Parts
            .DistinctBy(part => (part.Model, part.Agent))
            .Skip(1)
            .Any();

    private static CostRow RowOf(string key, string label, IReadOnlyCollection<CostRecord> records)
    {
        var usages = records
            .Select(record => record.Usage)
            .OfType<CopilotUsage>();

        return new CostRow(
            key,
            label,
            records.Sum(record => record.CopilotCalls ?? 0),
            records.Count,
            records.Count(record => record.CopilotCalls is null),
            records.Sum(record => record.DurationSeconds))
        {
            Usage = CopilotUsage.Sum(usages),
            // A job that ran no Copilot has nothing to miss.
            JobsWithoutUsage = records.Count(record => record.Usage is null && record.RanCopilot),
            Models = records
                .Where(record => record.ModelReason is not null)
                .GroupBy(record => (record.Model, Reason: record.ModelReason!))
                .Select(group => new ModelUse(group.Key.Model, group.Key.Reason, group.Count()))
                .OrderByDescending(use => use.Jobs)
                .ToList(),
        };
    }

    private static DateOnly LocalDay(CostRecord record, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(record.At, zone);

        return DateOnly.FromDateTime(local.DateTime);
    }

    private static (string Key, string Label) DayOf(CostRecord record, TimeZoneInfo zone)
    {
        var day = LocalDay(record, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return (day, day);
    }

    private static bool IsInRange(DateOnly day, CostRange range) =>
        (range.From is null || day >= range.From) && (range.To is null || day <= range.To);

    private static (string Key, string Label) ProjectOf(CostRecord record, RunnerConfig config)
    {
        var name = config.FindProject(record.Repo)?.Name ?? OtherProject;

        return (name, name);
    }

    /// <summary>
    /// A PR job (review, fix) belongs to the issue its PR links, the others to their own issue; a sub-issue
    /// belongs to its parent, so a parent's total includes its sub-issues.
    /// </summary>
    private sealed class IssueGrouper(CostInput input)
    {
        private readonly Dictionary<string, IssueSnapshot> _issues = input.Issues
            .DistinctBy(issue => KeyOf(issue.Repo, issue.Number), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(issue => KeyOf(issue.Repo, issue.Number), StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, PullSnapshot> _pulls = input.Pulls
            .DistinctBy(pull => KeyOf(pull.Repo, pull.Number), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pull => KeyOf(pull.Repo, pull.Number), StringComparer.OrdinalIgnoreCase);

        public (string Key, string Label) GroupOf(CostRecord record)
        {
            var key = KeyOf(record.Repo, record.Number);
            var isPullJob = record.Workflow is Workflows.Review or Workflows.Fix;
            if (isPullJob && _pulls.TryGetValue(key, out var pull) && pull.LinkedIssue is { Length: > 0 } linked)
            {
                key = linked;
            }

            _issues.TryGetValue(key, out var issue);
            if (issue?.Parent is { } parent)
            {
                key = KeyOf(issue.Repo, parent);
                _issues.TryGetValue(key, out issue);
            }

            var label = issue is null ? key : $"{key} {issue.Title}";

            return (key, label);
        }

        private static string KeyOf(string repo, int number) => new IssueRef(repo, number).ToString();
    }
}
