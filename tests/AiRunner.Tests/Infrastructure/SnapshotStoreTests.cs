using AiRunner.Core;
using AiRunner.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AiRunner.Tests.Infrastructure;

/// <summary>The snapshot tables in SQLite: rows come back whole, and an older database gets the tables.</summary>
public sealed class SnapshotStoreTests : IDisposable
{
    private static readonly DateTimeOffset Seen = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-snapshot-").FullName;

    public void Dispose() => FileSystem.DeleteDirectory(_folder);

    private string StateFile => Path.Combine(_folder, "state.db");

    private SqliteStateStore OpenStore() => new(StateFile, pooling: false);

    private static IssueSnapshot Issue(int number, string state = "open") =>
        new("o/r", number, "r", $"Issue {number}", state, ["ai", "ai:sub-issue"], ["bot"], Seen.AddHours(-1), Seen)
        {
            Parent = 12,
            SubOrder = 2,
            Component = "web",
            HtmlUrl = $"http://gb/o/r/issues/{number}",
            CreatedAt = Seen.AddDays(-3),
            ClosedAt = state == "closed" ? Seen.AddMinutes(-5) : null,
        };

    private static PullSnapshot Pull(int number) =>
        new("o/r", number, "r", "Add login", "closed", true, "ai/issue-12-2", "feat/issue-12", "abc123", Seen, Seen)
        {
            LinkedIssue = "o/r#21",
            HtmlUrl = $"http://gb/o/r/pull/{number}",
        };

    [Fact]
    public void Issue_and_pull_rows_round_trip()
    {
        var store = OpenStore();
        var issue = Issue(21, "closed");
        var pull = Pull(40);

        store.SaveIssues([issue]);
        store.SavePulls([pull]);

        var reopened = OpenStore();
        var readIssue = Assert.Single(reopened.ListIssues());
        Assert.Equal(issue.Labels, readIssue.Labels);
        Assert.Equal(issue.Assignees, readIssue.Assignees);
        Assert.Equal(issue with { Labels = readIssue.Labels, Assignees = readIssue.Assignees }, readIssue);
        Assert.Equal(pull, Assert.Single(reopened.ListPulls()));
    }

    [Fact]
    public void Saving_again_replaces_the_row()
    {
        var store = OpenStore();
        store.SaveIssues([Issue(21)]);

        store.SaveIssues([Issue(21, "closed")]);

        Assert.Equal("closed", Assert.Single(store.ListIssues()).State);
    }

    [Fact]
    public void Remove_except_keeps_the_given_numbers_of_that_project_and_repo_only()
    {
        var store = OpenStore();
        var otherRepo = Issue(5) with
        {
            Repo = "o/other"
        };
        var otherProject = Issue(6) with
        {
            Project = "s"
        };
        var otherProjectPull = Pull(42) with
        {
            Project = "s"
        };
        store.SaveIssues([Issue(1), Issue(2), Issue(3), otherRepo, otherProject]);
        store.SavePulls([Pull(40), Pull(41), otherProjectPull]);

        store.RemoveIssuesExcept("r", "o/r", new HashSet<int> { 2 });
        store.RemovePullsExcept("r", "o/r", new HashSet<int>());

        var left = store.ListIssues().Select(issue => (issue.Repo, issue.Number));
        Assert.Equal([("o/other", 5), ("o/r", 2), ("o/r", 6)], left);
        Assert.Equal(42, Assert.Single(store.ListPulls()).Number);
    }

    [Fact]
    public void An_old_database_without_the_snapshot_tables_gets_them()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = StateFile, Pooling = false }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE issue_seen (repo TEXT, issue INTEGER, fingerprint TEXT NOT NULL,
                    PRIMARY KEY (repo, issue));
                """;
            command.ExecuteNonQuery();
        }

        var store = OpenStore();
        store.SaveIssues([Issue(1)]);

        Assert.Single(store.ListIssues());
        Assert.Empty(store.ListPulls());
    }
}
