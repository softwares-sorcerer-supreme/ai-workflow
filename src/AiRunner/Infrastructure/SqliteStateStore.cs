using System.Globalization;
using AiRunner.Configuration;
using AiRunner.Core;
using Microsoft.Data.Sqlite;

namespace AiRunner.Infrastructure;

public static class JobStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

public sealed record StoredJob(long Id, JobRequest Request, string Status, int Attempts);

public sealed record ReviewState(string HeadSha, long? CommentId, string Result);

public sealed record CostEntry(DateTimeOffset At, string Repo, int Number, string Workflow, double DurationSeconds, string Result, string Detail);

/// <summary>
/// Small local state (design doc section 10). Deleting the file is safe: the runner reads
/// GitBucket again and finds its own comments by their markers.
/// </summary>
public interface IStateStore
{
    /// <summary>
    /// What the poller last saw of each issue it had nothing to do for (updated_at + labels),
    /// by issue number. An issue whose fingerprint is unchanged is not read again.
    /// </summary>
    IReadOnlyDictionary<int, string> GetIssueFingerprints(string repo);
    void SetIssueFingerprint(string repo, int issue, string fingerprint);

    long GetLastHandledComment(string repo, int issue);
    void SetLastHandledComment(string repo, int issue, long commentId);

    ReviewState? GetReview(string repo, int pr);
    void SetReview(string repo, int pr, ReviewState state);

    long AddJob(JobRequest request);
    StoredJob? GetJob(long id);
    StoredJob? FindJob(string key, string status);
    IReadOnlyList<StoredJob> ListJobs(string status);
    void UpdateJobPayload(long id, string? payload);
    /// <summary>Running also counts an attempt.</summary>
    void SetJobStatus(long id, string status, string? error = null);
    /// <summary>Deletes finished jobs older than the given time.</summary>
    void PruneJobs(DateTimeOffset finishedBefore);
    /// <summary>For a job that found nothing to do: no row is kept.</summary>
    void DeleteJob(long id);

    void AddCost(CostEntry entry);
}

public sealed class SqliteStateStore : IStateStore
{
    private readonly string _connectionString;

    public SqliteStateStore(RunnerConfig config) : this(config.ResolvePath(config.Paths.State)) { }

    /// <param name="pooling">False in tests: the file is released on close, without clearing every pool of the process.</param>
    public SqliteStateStore(string file, bool pooling = true)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = pooling }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS issue_seen (repo TEXT, issue INTEGER, fingerprint TEXT NOT NULL,
                PRIMARY KEY (repo, issue));
            CREATE TABLE IF NOT EXISTS issue_state (repo TEXT, issue INTEGER, last_handled_comment INTEGER NOT NULL,
                PRIMARY KEY (repo, issue));
            CREATE TABLE IF NOT EXISTS review_state (repo TEXT, pr INTEGER, head_sha TEXT NOT NULL, comment_id INTEGER,
                result TEXT NOT NULL, PRIMARY KEY (repo, pr));
            CREATE TABLE IF NOT EXISTS jobs (id INTEGER PRIMARY KEY AUTOINCREMENT, key TEXT NOT NULL, type TEXT NOT NULL,
                repo TEXT NOT NULL, number INTEGER NOT NULL, payload TEXT, status TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL, started_at TEXT, finished_at TEXT, error TEXT);
            CREATE INDEX IF NOT EXISTS ix_jobs_status ON jobs (status, key);
            CREATE TABLE IF NOT EXISTS cost_log (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, repo TEXT NOT NULL,
                number INTEGER NOT NULL, workflow TEXT NOT NULL, duration_s REAL NOT NULL, result TEXT NOT NULL, detail TEXT);
            """);
    }

    public IReadOnlyDictionary<int, string> GetIssueFingerprints(string repo)
    {
        using var conn = Open();
        using var cmd = Command(conn, "SELECT issue, fingerprint FROM issue_seen WHERE repo = $repo", ("$repo", repo));
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<int, string>();
        while (r.Read()) result[r.GetInt32(0)] = r.GetString(1);
        return result;
    }

    public void SetIssueFingerprint(string repo, int issue, string fingerprint) =>
        Execute("""
            INSERT INTO issue_seen (repo, issue, fingerprint) VALUES ($repo, $issue, $fp)
            ON CONFLICT(repo, issue) DO UPDATE SET fingerprint = $fp
            """, ("$repo", repo), ("$issue", issue), ("$fp", fingerprint));

    public long GetLastHandledComment(string repo, int issue) =>
        Scalar("SELECT last_handled_comment FROM issue_state WHERE repo = $repo AND issue = $issue",
            ("$repo", repo), ("$issue", issue)) is long id ? id : 0;

    public void SetLastHandledComment(string repo, int issue, long commentId) =>
        Execute("""
            INSERT INTO issue_state (repo, issue, last_handled_comment) VALUES ($repo, $issue, $id)
            ON CONFLICT(repo, issue) DO UPDATE SET last_handled_comment = MAX(last_handled_comment, $id)
            """, ("$repo", repo), ("$issue", issue), ("$id", commentId));

    public ReviewState? GetReview(string repo, int pr)
    {
        using var conn = Open();
        using var cmd = Command(conn, "SELECT head_sha, comment_id, result FROM review_state WHERE repo = $repo AND pr = $pr",
            ("$repo", repo), ("$pr", pr));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ReviewState(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2)) : null;
    }

    public void SetReview(string repo, int pr, ReviewState state) =>
        Execute("""
            INSERT INTO review_state (repo, pr, head_sha, comment_id, result) VALUES ($repo, $pr, $sha, $cid, $result)
            ON CONFLICT(repo, pr) DO UPDATE SET head_sha = $sha, comment_id = COALESCE($cid, comment_id), result = $result
            """, ("$repo", repo), ("$pr", pr), ("$sha", state.HeadSha), ("$cid", state.CommentId), ("$result", state.Result));

    public long AddJob(JobRequest request) =>
        (long)Scalar("""
            INSERT INTO jobs (key, type, repo, number, payload, status, created_at)
            VALUES ($key, $type, $repo, $number, $payload, 'pending', $now);
            SELECT last_insert_rowid();
            """,
            ("$key", request.Key), ("$type", request.Type.ToString()), ("$repo", request.Repo),
            ("$number", request.Number), ("$payload", request.Payload), ("$now", Time(DateTimeOffset.UtcNow)))!;

    public StoredJob? GetJob(long id) => QueryJobs("WHERE id = $id", ("$id", id)).FirstOrDefault();

    public StoredJob? FindJob(string key, string status) =>
        QueryJobs("WHERE key = $key AND status = $status ORDER BY id", ("$key", key), ("$status", status)).FirstOrDefault();

    public IReadOnlyList<StoredJob> ListJobs(string status) => QueryJobs("WHERE status = $status ORDER BY id", ("$status", status));

    public void UpdateJobPayload(long id, string? payload) =>
        Execute("UPDATE jobs SET payload = $payload WHERE id = $id", ("$id", id), ("$payload", payload));

    public void SetJobStatus(long id, string status, string? error = null)
    {
        var now = Time(DateTimeOffset.UtcNow);
        if (status == JobStatus.Running)
            Execute("UPDATE jobs SET status = $s, attempts = attempts + 1, started_at = $now WHERE id = $id",
                ("$id", id), ("$s", status), ("$now", now));
        else if (status == JobStatus.Pending)
            Execute("UPDATE jobs SET status = $s WHERE id = $id", ("$id", id), ("$s", status));
        else
            Execute("UPDATE jobs SET status = $s, finished_at = $now, error = $err WHERE id = $id",
                ("$id", id), ("$s", status), ("$now", now), ("$err", error));
    }

    public void DeleteJob(long id) => Execute("DELETE FROM jobs WHERE id = $id", ("$id", id));

    public void PruneJobs(DateTimeOffset finishedBefore) =>
        Execute("DELETE FROM jobs WHERE status IN ('done', 'failed') AND finished_at < $t", ("$t", Time(finishedBefore)));

    public void AddCost(CostEntry e) =>
        Execute("""
            INSERT INTO cost_log (at, repo, number, workflow, duration_s, result, detail)
            VALUES ($at, $repo, $number, $wf, $d, $result, $detail)
            """, ("$at", Time(e.At)), ("$repo", e.Repo), ("$number", e.Number), ("$wf", e.Workflow),
            ("$d", e.DurationSeconds), ("$result", e.Result), ("$detail", e.Detail));

    // ---- helpers ----

    private List<StoredJob> QueryJobs(string where, params (string, object?)[] args)
    {
        using var conn = Open();
        using var cmd = Command(conn, $"SELECT id, type, repo, number, payload, status, attempts FROM jobs {where}", args);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredJob>();
        while (r.Read())
        {
            var request = new JobRequest(Enum.Parse<JobType>(r.GetString(1)), r.GetString(2), r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4));
            list.Add(new StoredJob(r.GetInt64(0), request, r.GetString(5), r.GetInt32(6)));
        }
        return list;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static SqliteCommand Command(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Execute(string sql, params (string, object?)[] args)
    {
        using var conn = Open();
        using var cmd = Command(conn, sql, args);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var conn = Open();
        using var cmd = Command(conn, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
