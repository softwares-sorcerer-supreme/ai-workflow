using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Infrastructure;

/// <summary>Copilot calls in SQLite and cost.csv, and the upgrade of files written by an older runner.</summary>
public sealed class CostStorageTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-cost-").FullName;

    public void Dispose() => FileSystem.DeleteDirectory(_folder);

    private string StateFile => Path.Combine(_folder, "state.db");

    private string CostCsv => Path.Combine(_folder, "cost.csv");

    private static CostEntry Entry(int? copilotCalls) =>
        new(DateTimeOffset.Parse("2026-03-05T10:00:00Z"), "o/r", 7, "analyze", 61.4, "ok", "round 1", copilotCalls);

    private CostLog OpenCostLog(SqliteStateStore store)
    {
        var config = new RunnerConfig { Paths = new PathsConfig { Logs = _folder } };

        return new CostLog(config, store, NullLogger<CostLog>.Instance);
    }

    [Fact]
    public void An_old_database_without_copilot_calls_is_upgraded_and_its_cost_rows_still_read()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = StateFile, Pooling = false }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE cost_log (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, repo TEXT NOT NULL,
                    number INTEGER NOT NULL, workflow TEXT NOT NULL, duration_s REAL NOT NULL, result TEXT NOT NULL,
                    detail TEXT);
                INSERT INTO cost_log (at, repo, number, workflow, duration_s, result, detail)
                VALUES ('2026-01-01T10:00:00.0000000+00:00', 'o/r', 1, 'implement', 120, 'ok', 'PR #3');
                CREATE TABLE jobs (id INTEGER PRIMARY KEY AUTOINCREMENT, key TEXT NOT NULL, type TEXT NOT NULL,
                    repo TEXT NOT NULL, number INTEGER NOT NULL, payload TEXT, status TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, started_at TEXT,
                    finished_at TEXT, error TEXT);
                INSERT INTO jobs (key, type, repo, number, status, attempts, created_at)
                VALUES ('ImplementIssue:o/r#1', 'ImplementIssue', 'o/r', 1, 'done', 1, '2026-01-01T10:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var store = new SqliteStateStore(StateFile, pooling: false);
        var reopened = new SqliteStateStore(StateFile, pooling: false);

        var old = Assert.Single(store.ListCost());
        Assert.Equal("implement", old.Workflow);
        Assert.Equal(120, old.DurationSeconds);
        Assert.Null(old.CopilotCalls);

        var job = Assert.Single(reopened.QueryHistory(new JobQuery()).Items);
        Assert.Null(job.CopilotCalls);

        reopened.AddCost(Entry(copilotCalls: 3));
        Assert.Equal([null, 3], store.ListCost().Select(cost => cost.CopilotCalls));
    }

    [Fact]
    public void A_finished_job_keeps_its_copilot_calls_and_an_unknown_count_stays_null()
    {
        var store = new SqliteStateStore(StateFile, pooling: false);
        var known = store.AddJob(new JobRequest(JobType.AnalyzeIssue, "o/r", 1));
        var unknown = store.AddJob(new JobRequest(JobType.AnalyzeIssue, "o/r", 2));

        store.SetJobResult(known, new JobResult("ok", "round 1", 1));
        store.SetJobResult(unknown, new JobResult("failed", "boom"));

        Assert.Equal(1, store.GetRecord(known)!.CopilotCalls);
        Assert.Null(store.GetRecord(unknown)!.CopilotCalls);
    }

    private const string Header =
        "date,repo,number,workflow,duration_s,result,detail,copilot_calls," +
        "nano_aiu,input_tokens,cache_read_tokens,cache_write_tokens,output_tokens";

    private static readonly CopilotUsage Usage = new(677165000, 4, 0, 53842, 82);

    [Fact]
    public void A_new_cost_csv_has_copilot_calls_then_the_usage_as_its_last_columns()
    {
        var store = new SqliteStateStore(StateFile, pooling: false);
        var costLog = OpenCostLog(store);

        costLog.Record(Entry(copilotCalls: 3) with { Usage = Usage });
        costLog.Record(Entry(copilotCalls: null));

        var lines = File.ReadAllLines(CostCsv);
        Assert.Equal(Header, lines[0]);
        Assert.Equal("2026-03-05 10:00:00,o/r,7,analyze,61,ok,round 1,3,677165000,4,0,53842,82", lines[1]);
        Assert.Equal("2026-03-05 10:00:00,o/r,7,analyze,61,ok,round 1,,,,,,", lines[2]);
    }

    [Theory]
    [InlineData("date,repo,number,workflow,duration_s,result,detail")]
    [InlineData("date,repo,number,workflow,duration_s,result,detail,copilot_calls")]
    public void An_old_cost_csv_gets_the_new_header_and_keeps_its_rows(string oldHeader)
    {
        File.WriteAllText(CostCsv, oldHeader + "\n2026-01-01 10:00:00,o/r,1,implement,120,ok,PR #3\n");
        var store = new SqliteStateStore(StateFile, pooling: false);

        OpenCostLog(store).Record(Entry(copilotCalls: 2));

        var lines = File.ReadAllLines(CostCsv);
        Assert.Equal(Header, lines[0]);
        Assert.Equal("2026-01-01 10:00:00,o/r,1,implement,120,ok,PR #3", lines[1]);
        Assert.Equal("2026-03-05 10:00:00,o/r,7,analyze,61,ok,round 1,2,,,,,", lines[2]);
    }

    [Fact]
    public void The_usage_of_a_cost_row_is_read_back_and_a_row_without_it_stays_null()
    {
        var store = new SqliteStateStore(StateFile, pooling: false);

        store.AddCost(Entry(copilotCalls: 1) with { Usage = Usage });
        store.AddCost(Entry(copilotCalls: 1));

        Assert.Equal([Usage, null], store.ListCost().Select(cost => cost.Usage));
    }
}
