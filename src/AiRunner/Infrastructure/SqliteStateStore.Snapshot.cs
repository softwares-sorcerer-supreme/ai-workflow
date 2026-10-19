using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>What the dashboard may read of the snapshot. No write method, on purpose.</summary>
public interface ISnapshotReader
{
    IReadOnlyList<IssueSnapshot> ListIssues();

    IReadOnlyList<PullSnapshot> ListPulls();
}

/// <summary>
/// The snapshot: the issues and PRs the poller last saw, kept only for the dashboard (docs/adr/0001: the dashboard
/// makes no GitBucket calls). It is a cache: deleting the database is safe, the next polls fill it again, and no
/// planner decision reads it.
/// </summary>
public interface ISnapshotStore : ISnapshotReader
{
    /// <summary>Adds the issues or replaces the rows that have the same repo and number.</summary>
    void SaveIssues(IReadOnlyList<IssueSnapshot> issues);

    void SavePulls(IReadOnlyList<PullSnapshot> pulls);

    /// <summary>Deletes every issue row of the repo whose number is not in <paramref name="keep"/>.</summary>
    void RemoveIssuesExcept(string repo, IReadOnlySet<int> keep);

    void RemovePullsExcept(string repo, IReadOnlySet<int> keep);
}

public sealed partial class SqliteStateStore : ISnapshotStore
{
    /// <summary>Lists (labels, assignees) are stored one name per line: names have no line breaks.</summary>
    private const char ListSeparator = '\n';

    private void CreateSnapshotTables() =>
        Execute("""
            CREATE TABLE IF NOT EXISTS issue_snapshot (repo TEXT NOT NULL, number INTEGER NOT NULL, project TEXT NOT NULL,
                title TEXT NOT NULL, state TEXT NOT NULL, labels TEXT NOT NULL, assignees TEXT NOT NULL, parent INTEGER,
                sub_order INTEGER, component TEXT, html_url TEXT, created_at TEXT, updated_at TEXT NOT NULL, closed_at TEXT,
                seen_at TEXT NOT NULL, PRIMARY KEY (repo, number));
            CREATE TABLE IF NOT EXISTS pull_snapshot (repo TEXT NOT NULL, number INTEGER NOT NULL, project TEXT NOT NULL,
                title TEXT NOT NULL, state TEXT NOT NULL, merged INTEGER NOT NULL, head_ref TEXT NOT NULL,
                base_ref TEXT NOT NULL, head_sha TEXT NOT NULL, linked_issue TEXT, html_url TEXT, updated_at TEXT NOT NULL,
                seen_at TEXT NOT NULL, PRIMARY KEY (repo, number));
            """);

    public void SaveIssues(IReadOnlyList<IssueSnapshot> issues)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        foreach (var issue in issues)
        {
            var labels = string.Join(ListSeparator, issue.Labels);
            var assignees = string.Join(ListSeparator, issue.Assignees);
            var createdAt = OptionalTime(issue.CreatedAt);
            var updatedAt = Time(issue.UpdatedAt);
            var closedAt = OptionalTime(issue.ClosedAt);
            var seenAt = Time(issue.SeenAt);

            using var command = Command(
                connection,
                """
                INSERT OR REPLACE INTO issue_snapshot (repo, number, project, title, state, labels, assignees, parent,
                    sub_order, component, html_url, created_at, updated_at, closed_at, seen_at)
                VALUES ($repo, $number, $project, $title, $state, $labels, $assignees, $parent, $order, $component,
                    $url, $created, $updated, $closed, $seen)
                """,
                ("$repo", issue.Repo),
                ("$number", issue.Number),
                ("$project", issue.Project),
                ("$title", issue.Title),
                ("$state", issue.State),
                ("$labels", labels),
                ("$assignees", assignees),
                ("$parent", issue.Parent),
                ("$order", issue.SubOrder),
                ("$component", issue.Component),
                ("$url", issue.HtmlUrl),
                ("$created", createdAt),
                ("$updated", updatedAt),
                ("$closed", closedAt),
                ("$seen", seenAt));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SavePulls(IReadOnlyList<PullSnapshot> pulls)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        foreach (var pull in pulls)
        {
            var updatedAt = Time(pull.UpdatedAt);
            var seenAt = Time(pull.SeenAt);

            using var command = Command(
                connection,
                """
                INSERT OR REPLACE INTO pull_snapshot (repo, number, project, title, state, merged, head_ref, base_ref,
                    head_sha, linked_issue, html_url, updated_at, seen_at)
                VALUES ($repo, $number, $project, $title, $state, $merged, $head, $base, $sha, $linked, $url, $updated,
                    $seen)
                """,
                ("$repo", pull.Repo),
                ("$number", pull.Number),
                ("$project", pull.Project),
                ("$title", pull.Title),
                ("$state", pull.State),
                ("$merged", pull.IsMerged ? 1 : 0),
                ("$head", pull.HeadRef),
                ("$base", pull.BaseRef),
                ("$sha", pull.HeadSha),
                ("$linked", pull.LinkedIssue),
                ("$url", pull.HtmlUrl),
                ("$updated", updatedAt),
                ("$seen", seenAt));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void RemoveIssuesExcept(string repo, IReadOnlySet<int> keep) => RemoveExcept("issue_snapshot", repo, keep);

    public void RemovePullsExcept(string repo, IReadOnlySet<int> keep) => RemoveExcept("pull_snapshot", repo, keep);

    public IReadOnlyList<IssueSnapshot> ListIssues()
    {
        using var connection = Open();
        using var command = Command(
            connection,
            """
            SELECT repo, number, project, title, state, labels, assignees, parent, sub_order, component, html_url,
                created_at, updated_at, closed_at, seen_at
            FROM issue_snapshot ORDER BY repo, number
            """);
        using var reader = command.ExecuteReader();

        var issues = new List<IssueSnapshot>();
        while (reader.Read())
        {
            var labels = SplitList(reader.GetString(5));
            var assignees = SplitList(reader.GetString(6));
            var updatedAt = ParseTime(reader.GetString(12));
            var seenAt = ParseTime(reader.GetString(14));

            issues.Add(new IssueSnapshot(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                labels,
                assignees,
                updatedAt,
                seenAt)
            {
                Parent = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                SubOrder = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Component = reader.IsDBNull(9) ? null : reader.GetString(9),
                HtmlUrl = reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedAt = reader.IsDBNull(11) ? null : ParseTime(reader.GetString(11)),
                ClosedAt = reader.IsDBNull(13) ? null : ParseTime(reader.GetString(13)),
            });
        }

        return issues;
    }

    public IReadOnlyList<PullSnapshot> ListPulls()
    {
        using var connection = Open();
        using var command = Command(
            connection,
            """
            SELECT repo, number, project, title, state, merged, head_ref, base_ref, head_sha, linked_issue, html_url,
                updated_at, seen_at
            FROM pull_snapshot ORDER BY repo, number
            """);
        using var reader = command.ExecuteReader();

        var pulls = new List<PullSnapshot>();
        while (reader.Read())
        {
            var updatedAt = ParseTime(reader.GetString(11));
            var seenAt = ParseTime(reader.GetString(12));

            pulls.Add(new PullSnapshot(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5) != 0,
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                updatedAt,
                seenAt)
            {
                LinkedIssue = reader.IsDBNull(9) ? null : reader.GetString(9),
                HtmlUrl = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }

        return pulls;
    }

    private void RemoveExcept(string table, string repo, IReadOnlySet<int> keep)
    {
        var names = keep.Select((number, index) => ($"$n{index}", (object?)number)).ToList();
        var placeholders = string.Join(", ", names.Select(name => name.Item1));
        var notIn = names.Count == 0 ? "" : $" AND number NOT IN ({placeholders})";
        var args = names.Prepend(("$repo", repo)).ToArray();

        Execute($"DELETE FROM {table} WHERE repo = $repo{notIn}", args);
    }

    private static string? OptionalTime(DateTimeOffset? time) => time is { } value ? Time(value) : null;

    private static List<string> SplitList(string text) =>
        text.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
}
