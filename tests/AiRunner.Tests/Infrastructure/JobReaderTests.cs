using AiRunner.Core;
using AiRunner.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AiRunner.Tests.Infrastructure;

/// <summary>The job history the dashboard reads from SQLite, and the upgrade of a database from before slice 2.</summary>
public sealed class JobReaderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-jobs-").FullName;

    public void Dispose() => FileSystem.DeleteDirectory(_folder);

    private string StateFile => Path.Combine(_folder, "state.db");

    private SqliteStateStore OpenStore() => new(StateFile, pooling: false);

    private long AddFinishedJob(SqliteStateStore store, JobRequest request, string status = JobStatus.Done)
    {
        var id = store.AddJob(request);
        store.SetJobStatus(id, JobStatus.Running);
        store.SetJobStatus(id, status, status == JobStatus.Failed ? "boom" : null);

        return id;
    }

    [Fact]
    public void An_old_database_without_log_dir_is_upgraded_and_its_rows_still_read()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = StateFile, Pooling = false }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE jobs (id INTEGER PRIMARY KEY AUTOINCREMENT, key TEXT NOT NULL, type TEXT NOT NULL,
                    repo TEXT NOT NULL, number INTEGER NOT NULL, payload TEXT, status TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, started_at TEXT,
                    finished_at TEXT, error TEXT);
                INSERT INTO jobs (key, type, repo, number, status, attempts, created_at, started_at, finished_at)
                VALUES ('AnalyzeIssue:o/r#1', 'AnalyzeIssue', 'o/r', 1, 'done', 1,
                    '2026-01-01T10:00:00.0000000+00:00', '2026-01-01T10:00:01.0000000+00:00',
                    '2026-01-01T10:01:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var store = OpenStore();
        var reopened = OpenStore();

        var page = store.QueryHistory(new JobQuery(Page: 1, PageSize: 10));
        var job = Assert.Single(page.Items);
        Assert.Equal("AnalyzeIssue", job.Type);
        Assert.Null(job.LogDir);
        Assert.Equal(1, page.Total);

        reopened.SetJobLogDir(job.Id, "20260101-100001-o-r-1-AnalyzeIssue");
        Assert.Equal("20260101-100001-o-r-1-AnalyzeIssue", store.GetRecord(job.Id)!.LogDir);
    }

    [Fact]
    public void The_queue_lists_pending_jobs_in_the_order_they_were_added()
    {
        var store = OpenStore();
        var first = store.AddJob(new JobRequest(JobType.AnalyzeIssue, "o/r", 1));
        var second = store.AddJob(new JobRequest(JobType.ReviewPullRequest, "o/r", 7));
        AddFinishedJob(store, new JobRequest(JobType.ImplementIssue, "o/r", 3));

        var queue = store.ListQueue();

        Assert.Equal([first, second], queue.Select(job => job.Id));
        Assert.All(queue, job => Assert.Equal(JobStatus.Pending, job.Status));
    }

    [Fact]
    public void History_is_newest_first_and_pages()
    {
        var store = OpenStore();
        var ids = Enumerable.Range(1, 5)
            .Select(number => AddFinishedJob(store, new JobRequest(JobType.AnalyzeIssue, "o/r", number)))
            .ToList();

        var firstPage = store.QueryHistory(new JobQuery(Page: 1, PageSize: 2));
        var thirdPage = store.QueryHistory(new JobQuery(Page: 3, PageSize: 2));

        Assert.Equal(5, firstPage.Total);
        Assert.Equal([ids[4], ids[3]], firstPage.Items.Select(job => job.Id));
        Assert.Equal([ids[0]], thirdPage.Items.Select(job => job.Id));
    }

    [Fact]
    public void History_filters_by_status_type_and_repos()
    {
        var store = OpenStore();
        AddFinishedJob(store, new JobRequest(JobType.AnalyzeIssue, "o/a", 1));
        var failed = AddFinishedJob(store, new JobRequest(JobType.AnalyzeIssue, "o/b", 2), JobStatus.Failed);
        AddFinishedJob(store, new JobRequest(JobType.ImplementIssue, "o/b", 3), JobStatus.Failed);

        var byStatus = store.QueryHistory(new JobQuery(Status: JobStatus.Failed, Page: 1, PageSize: 10));
        var byType = store.QueryHistory(new JobQuery(Type: "AnalyzeIssue", Page: 1, PageSize: 10));
        var byRepos = store.QueryHistory(new JobQuery(Repos: ["o/b"], Page: 1, PageSize: 10));
        var combined = store.QueryHistory(new JobQuery(
            Status: JobStatus.Failed,
            Type: "AnalyzeIssue",
            Repos: ["o/b"],
            Page: 1,
            PageSize: 10));
        var noRepos = store.QueryHistory(new JobQuery(Repos: [], Page: 1, PageSize: 10));

        Assert.Equal(2, byStatus.Total);
        Assert.Equal(2, byType.Total);
        Assert.Equal(2, byRepos.Total);
        Assert.Equal([failed], combined.Items.Select(job => job.Id));
        Assert.Equal(0, noRepos.Total);
    }

    [Fact]
    public void A_job_carries_its_error_and_the_result_stored_when_it_ended()
    {
        var store = OpenStore();
        var id = AddFinishedJob(store, new JobRequest(JobType.ImplementIssue, "o/r", 4), JobStatus.Failed);
        store.SetJobResult(id, "failed", "no PR");

        var job = store.GetRecord(id);

        Assert.NotNull(job);
        Assert.Equal("boom", job.Error);
        Assert.Equal("failed", job.Result);
        Assert.Equal("no PR", job.Detail);
        Assert.Null(store.GetRecord(12345));
    }

    [Fact]
    public void A_job_that_has_not_ended_has_no_result()
    {
        var store = OpenStore();
        var id = store.AddJob(new JobRequest(JobType.AnalyzeIssue, "o/r", 1));

        var job = store.GetRecord(id);

        Assert.NotNull(job);
        Assert.Null(job.Result);
        Assert.Null(job.StartedAt);
    }
}
