using System.Globalization;
using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>One row of the cost log. <c>CopilotCalls</c> is null for old rows and for failed jobs.</summary>
public sealed record CostRecord(
    DateTimeOffset At,
    string Repo,
    int Number,
    string Workflow,
    double DurationSeconds,
    int? CopilotCalls);

public enum CostGrouping
{
    Day,
    Project,
    Issue,
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
/// so a total is never presented as complete when it is not.
/// </summary>
public sealed record CostRow(
    string Key,
    string Label,
    int Calls,
    int Jobs,
    int JobsWithoutCount,
    double DurationSeconds);

public sealed record CostResult(IReadOnlyList<CostRow> Rows, CostRow Total);

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

        List<CostRow> groups;
        switch (query.By)
        {
            case CostGrouping.Day:
                groups = GroupBy(inRange, record => DayOf(record, input.Zone));

                break;
            case CostGrouping.Project:
                groups = GroupBy(inRange, record => ProjectOf(record, input.Config));

                break;
            default:
                var grouper = new IssueGrouper(input);
                groups = GroupBy(inRange, grouper.GroupOf);

                break;
        }

        var rows = query.By == CostGrouping.Day
            ? groups.OrderBy(row => row.Key, StringComparer.Ordinal).ToList()
            : groups
                .OrderByDescending(row => row.Calls)
                .ThenByDescending(row => row.DurationSeconds)
                .ThenBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

        return new CostResult(rows, RowOf("total", "Total", inRange));
    }

    private static List<CostRow> GroupBy(
        IReadOnlyList<CostRecord> records,
        Func<CostRecord, (string Key, string Label)> groupOf)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var labels = new Dictionary<string, string>(comparer);
        var byKey = new Dictionary<string, List<CostRecord>>(comparer);

        foreach (var record in records)
        {
            var (key, label) = groupOf(record);
            labels.TryAdd(key, label);
            if (!byKey.TryGetValue(key, out var group))
            {
                group = [];
                byKey[key] = group;
            }

            group.Add(record);
        }

        return byKey
            .Select(entry => RowOf(entry.Key, labels[entry.Key], entry.Value))
            .ToList();
    }

    private static CostRow RowOf(string key, string label, IReadOnlyCollection<CostRecord> records) =>
        new(
            key,
            label,
            records.Sum(record => record.CopilotCalls ?? 0),
            records.Count,
            records.Count(record => record.CopilotCalls is null),
            records.Sum(record => record.DurationSeconds));

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
            .GroupBy(issue => KeyOf(issue.Repo, issue.Number), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, PullSnapshot> _pulls = input.Pulls
            .GroupBy(pull => KeyOf(pull.Repo, pull.Number), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

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
