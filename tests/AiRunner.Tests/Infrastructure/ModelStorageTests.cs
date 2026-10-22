using AiRunner.Core;
using AiRunner.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AiRunner.Tests.Infrastructure;

/// <summary>
/// jobs.model, jobs.model_reason and the same two columns of cost_log, and the upgrade of an old ai-runner.db.
/// </summary>
public sealed class ModelStorageTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-model-").FullName;

    public void Dispose() => FileSystem.DeleteDirectory(_folder);

    private string StateFile => Path.Combine(_folder, "state.db");

    private void WriteOldDatabase()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = StateFile, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();

        // The tables as the runner made them before model selection: no model columns anywhere.
        command.CommandText = """
            CREATE TABLE jobs (id INTEGER PRIMARY KEY AUTOINCREMENT, key TEXT NOT NULL, type TEXT NOT NULL,
                repo TEXT NOT NULL, number INTEGER NOT NULL, payload TEXT, status TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, started_at TEXT, finished_at TEXT,
                error TEXT, log_dir TEXT, result TEXT, detail TEXT, copilot_calls INTEGER);
            INSERT INTO jobs (key, type, repo, number, status, attempts, created_at, result, copilot_calls)
            VALUES ('ImplementIssue:o/r#1', 'ImplementIssue', 'o/r', 1, 'done', 1,
                '2026-01-01T10:00:00.0000000+00:00', 'ok', 2);
            CREATE TABLE cost_log (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, repo TEXT NOT NULL,
                number INTEGER NOT NULL, workflow TEXT NOT NULL, duration_s REAL NOT NULL, result TEXT NOT NULL,
                detail TEXT, copilot_calls INTEGER);
            INSERT INTO cost_log (at, repo, number, workflow, duration_s, result, detail, copilot_calls)
            VALUES ('2026-01-01T10:00:00.0000000+00:00', 'o/r', 1, 'implement', 120, 'ok', 'PR #3', 2);
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void An_old_database_without_the_model_columns_is_upgraded_at_startup_and_old_rows_read_null()
    {
        WriteOldDatabase();

        var store = new SqliteStateStore(StateFile, pooling: false);

        var oldJob = Assert.Single(store.QueryHistory(new JobQuery()).Items);
        Assert.Null(oldJob.Model);
        Assert.Null(oldJob.ModelReason);
        Assert.Equal(2, oldJob.CopilotCalls);
        var oldCost = Assert.Single(store.ListCost());
        Assert.Null(oldCost.Model);
        Assert.Null(oldCost.ModelReason);

        // The columns work: a new job and a new cost row keep their model.
        var jobId = store.AddJob(new JobRequest(JobType.ImplementIssue, "o/r", 2));
        store.SetJobResult(jobId, new JobResult("ok", "x", 1, "claude-opus-5.5", ModelResolver.ReasonOverride));
        store.AddCost(new CostEntry(
            DateTimeOffset.Parse("2026-03-05T10:00:00Z"), "o/r", 2, "implement", 5, "ok", "x", 1)
        {
            Model = "claude-opus-5.5",
            ModelReason = ModelResolver.ReasonOverride,
        });

        var record = store.GetRecord(jobId)!;
        Assert.Equal("claude-opus-5.5", record.Model);
        Assert.Equal(ModelResolver.ReasonOverride, record.ModelReason);
        var newCost = store.ListCost().Last();
        Assert.Equal(("claude-opus-5.5", ModelResolver.ReasonOverride), (newCost.Model, newCost.ModelReason));
    }

    [Fact]
    public void Opening_an_upgraded_database_again_changes_nothing()
    {
        WriteOldDatabase();
        _ = new SqliteStateStore(StateFile, pooling: false);

        var reopened = new SqliteStateStore(StateFile, pooling: false);

        Assert.Single(reopened.QueryHistory(new JobQuery()).Items);
    }

    [Fact]
    public void A_job_that_ran_no_copilot_has_no_model()
    {
        var store = new SqliteStateStore(StateFile, pooling: false);
        var jobId = store.AddJob(new JobRequest(JobType.AnalyzeIssue, "o/r", 1));

        store.SetJobResult(jobId, new JobResult("ok", "x"));

        var record = store.GetRecord(jobId)!;
        Assert.Null(record.Model);
        Assert.Null(record.ModelReason);
    }
}
