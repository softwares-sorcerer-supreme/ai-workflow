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

/// <summary>
/// The review row of a PR. <c>HeadSha</c> is the last head the review job handled (reviewed, skipped or failed), so it
/// is not planned again. The other values are left as they are when a write does not set them (null).
/// </summary>
public sealed record ReviewState(string HeadSha, long? CommentId, string Result)
{
    /// <summary>Result when the head was not reviewed because it had only skip_paths files (or none).</summary>
    public const string Skipped = "skipped";

    /// <summary>Result when the review job failed on the head (it is not retried on every poll).</summary>
    public const string Failed = "failed";

    /// <summary>Result of a row written before the first review, when a full review was asked for by label.</summary>
    public const string Pending = "pending";

    /// <summary>Head of the last posted review: the base of the next incremental review.</summary>
    public string? ReviewedSha { get; init; }

    /// <summary>Automatic reviews posted so far (limits.max_reviews); null on a row from an older runner.</summary>
    public int? ReviewCount { get; init; }

    /// <summary>Head that got the full review the label ai-review-full asked for.</summary>
    public string? FullReviewSha { get; init; }
}

/// <summary>How a job ended, as the dashboard reads it from the job row.</summary>
public sealed record JobResult(string Result, string Detail, int? CopilotCalls = null);

/// <param name="Usage">Tokens and credits from the run's usage files, or null when there were none.</param>
public sealed record CostEntry(
    DateTimeOffset At,
    string Repo,
    int Number,
    string Workflow,
    double DurationSeconds,
    string Result,
    string Detail,
    int? CopilotCalls = null,
    CopilotUsage? Usage = null);

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

    ReviewState? GetReview(string repo, int pullNumber);
    void SetReview(string repo, int pullNumber, ReviewState reviewState);

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

    /// <summary>Remembers the job's log folder (a name under logs/jobs/) so the dashboard can open it.</summary>
    void SetJobLogDir(long id, string folderName);

    /// <summary>How the job ended, for the dashboard (the cost log has no job id to read it from).</summary>
    void SetJobResult(long id, JobResult jobResult);
}

public sealed partial class SqliteStateStore : IStateStore, IJobReader, ICostReader
{
    /// <summary>The usage columns of cost_log as SQL ("nano_aiu, input_tokens, ..."), from CopilotUsage.ColumnNames.</summary>
    private static readonly string UsageColumnList = string.Join(", ", CopilotUsage.ColumnNames);

    /// <summary>Their parameter names ("$usage0, $usage1, ..."), in the same order.</summary>
    private static readonly string UsageParameterList = string.Join(
        ", ",
        Enumerable.Range(0, CopilotUsage.ColumnNames.Count).Select(index => $"$usage{index}"));

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

        AddColumnIfMissing("jobs", "log_dir", "TEXT");
        AddColumnIfMissing("jobs", "result", "TEXT");
        AddColumnIfMissing("jobs", "detail", "TEXT");
        AddColumnIfMissing("jobs", "copilot_calls", "INTEGER");
        AddColumnIfMissing("cost_log", "copilot_calls", "INTEGER");
        AddColumnIfMissing("review_state", "reviewed_sha", "TEXT");
        AddColumnIfMissing("review_state", "review_count", "INTEGER");
        AddColumnIfMissing("review_state", "full_sha", "TEXT");
        foreach (var usageColumn in CopilotUsage.ColumnNames)
        {
            AddColumnIfMissing("cost_log", usageColumn, "INTEGER");
        }

        CreateSnapshotTables();
    }

    public IReadOnlyDictionary<int, string> GetIssueFingerprints(string repo)
    {
        using var connection = Open();
        using var command = Command(
            connection,
            "SELECT issue, fingerprint FROM issue_seen WHERE repo = $repo",
            ("$repo", repo));
        using var reader = command.ExecuteReader();

        var result = new Dictionary<int, string>();
        while (reader.Read())
        {
            result[reader.GetInt32(0)] = reader.GetString(1);
        }

        return result;
    }

    public void SetIssueFingerprint(string repo, int issue, string fingerprint) =>
        Execute(
            """
            INSERT INTO issue_seen (repo, issue, fingerprint) VALUES ($repo, $issue, $fp)
            ON CONFLICT(repo, issue) DO UPDATE SET fingerprint = $fp
            """,
            ("$repo", repo),
            ("$issue", issue),
            ("$fp", fingerprint));

    public long GetLastHandledComment(string repo, int issue) =>
        Scalar(
            "SELECT last_handled_comment FROM issue_state WHERE repo = $repo AND issue = $issue",
            ("$repo", repo),
            ("$issue", issue)) is long id ? id : 0;

    public void SetLastHandledComment(string repo, int issue, long commentId) =>
        Execute(
            """
            INSERT INTO issue_state (repo, issue, last_handled_comment) VALUES ($repo, $issue, $id)
            ON CONFLICT(repo, issue) DO UPDATE SET last_handled_comment = MAX(last_handled_comment, $id)
            """,
            ("$repo", repo),
            ("$issue", issue),
            ("$id", commentId));

    public ReviewState? GetReview(string repo, int pullNumber)
    {
        using var connection = Open();
        using var command = Command(
            connection,
            """
            SELECT head_sha, comment_id, result, reviewed_sha, review_count, full_sha
            FROM review_state WHERE repo = $repo AND pr = $pr
            """,
            ("$repo", repo),
            ("$pr", pullNumber));
        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        var headSha = reader.GetString(0);
        long? commentId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        var result = reader.GetString(2);

        return new ReviewState(headSha, commentId, result)
        {
            ReviewedSha = reader.IsDBNull(3) ? null : reader.GetString(3),
            ReviewCount = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            FullReviewSha = reader.IsDBNull(5) ? null : reader.GetString(5),
        };
    }

    public void SetReview(string repo, int pullNumber, ReviewState reviewState) =>
        Execute(
            """
            INSERT INTO review_state (repo, pr, head_sha, comment_id, result, reviewed_sha, review_count, full_sha)
            VALUES ($repo, $pr, $sha, $cid, $result, $reviewed, $count, $full)
            ON CONFLICT(repo, pr) DO UPDATE SET head_sha = $sha, comment_id = COALESCE($cid, comment_id), result = $result,
                reviewed_sha = COALESCE($reviewed, reviewed_sha), review_count = COALESCE($count, review_count),
                full_sha = COALESCE($full, full_sha)
            """,
            ("$repo", repo),
            ("$pr", pullNumber),
            ("$sha", reviewState.HeadSha),
            ("$cid", reviewState.CommentId),
            ("$result", reviewState.Result),
            ("$reviewed", reviewState.ReviewedSha),
            ("$count", reviewState.ReviewCount),
            ("$full", reviewState.FullReviewSha));

    public long AddJob(JobRequest request)
    {
        var typeName = request.Type.ToString();
        var now = Time(DateTimeOffset.UtcNow);

        return (long)Scalar(
            """
            INSERT INTO jobs (key, type, repo, number, payload, status, created_at)
            VALUES ($key, $type, $repo, $number, $payload, 'pending', $now);
            SELECT last_insert_rowid();
            """,
            ("$key", request.Key),
            ("$type", typeName),
            ("$repo", request.Repo),
            ("$number", request.Number),
            ("$payload", request.Payload),
            ("$now", now))!;
    }

    public StoredJob? GetJob(long id) => QueryJobs("WHERE id = $id", ("$id", id)).FirstOrDefault();

    public StoredJob? FindJob(string key, string status) =>
        QueryJobs("WHERE key = $key AND status = $status ORDER BY id", ("$key", key), ("$status", status))
            .FirstOrDefault();

    public IReadOnlyList<StoredJob> ListJobs(string status) =>
        QueryJobs("WHERE status = $status ORDER BY id", ("$status", status));

    public void UpdateJobPayload(long id, string? payload) =>
        Execute("UPDATE jobs SET payload = $payload WHERE id = $id", ("$id", id), ("$payload", payload));

    public void SetJobStatus(long id, string status, string? error = null)
    {
        var now = Time(DateTimeOffset.UtcNow);

        if (status == JobStatus.Running)
        {
            Execute(
                "UPDATE jobs SET status = $s, attempts = attempts + 1, started_at = $now WHERE id = $id",
                ("$id", id),
                ("$s", status),
                ("$now", now));
        }
        else if (status == JobStatus.Pending)
        {
            Execute("UPDATE jobs SET status = $s WHERE id = $id", ("$id", id), ("$s", status));
        }
        else
        {
            Execute(
                "UPDATE jobs SET status = $s, finished_at = $now, error = $err WHERE id = $id",
                ("$id", id),
                ("$s", status),
                ("$now", now),
                ("$err", error));
        }
    }

    public void DeleteJob(long id) => Execute("DELETE FROM jobs WHERE id = $id", ("$id", id));

    public void PruneJobs(DateTimeOffset finishedBefore)
    {
        var finishedBeforeText = Time(finishedBefore);
        Execute(
            "DELETE FROM jobs WHERE status IN ('done', 'failed') AND finished_at < $t",
            ("$t", finishedBeforeText));
    }

    public void AddCost(CostEntry entry)
    {
        var at = Time(entry.At);
        var usageValues = entry.Usage?.Values();
        var usageParameters = Enumerable
            .Range(0, CopilotUsage.ColumnNames.Count)
            .Select(index => ($"$usage{index}", (object?)usageValues?[index]));
        (string, object?)[] parameters =
        [
            ("$at", at),
            ("$repo", entry.Repo),
            ("$number", entry.Number),
            ("$wf", entry.Workflow),
            ("$d", entry.DurationSeconds),
            ("$result", entry.Result),
            ("$detail", entry.Detail),
            ("$calls", entry.CopilotCalls),
            .. usageParameters,
        ];

        Execute(
            $"""
            INSERT INTO cost_log (at, repo, number, workflow, duration_s, result, detail, copilot_calls, {UsageColumnList})
            VALUES ($at, $repo, $number, $wf, $d, $result, $detail, $calls, {UsageParameterList})
            """,
            parameters);
    }

    public IReadOnlyList<CostRecord> ListCost()
    {
        using var connection = Open();
        using var command = Command(
            connection,
            $"""
            SELECT at, repo, number, workflow, duration_s, copilot_calls, {UsageColumnList}
            FROM cost_log ORDER BY id
            """);
        using var reader = command.ExecuteReader();

        const int firstUsageOrdinal = 6;
        var records = new List<CostRecord>();
        while (reader.Read())
        {
            CopilotUsage? usage = null;
            if (!reader.IsDBNull(firstUsageOrdinal))
            {
                var usageValues = Enumerable
                    .Range(firstUsageOrdinal, CopilotUsage.ColumnNames.Count)
                    .Select(reader.GetInt64)
                    .ToList();
                usage = CopilotUsage.FromValues(usageValues);
            }

            records.Add(new CostRecord(
                ParseTime(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                usage));
        }

        return records;
    }

    public void SetJobLogDir(long id, string folderName) =>
        Execute("UPDATE jobs SET log_dir = $dir WHERE id = $id", ("$id", id), ("$dir", folderName));

    public void SetJobResult(long id, JobResult jobResult) =>
        Execute(
            "UPDATE jobs SET result = $result, detail = $detail, copilot_calls = $calls WHERE id = $id",
            ("$id", id),
            ("$result", jobResult.Result),
            ("$detail", jobResult.Detail),
            ("$calls", jobResult.CopilotCalls));

    public IReadOnlyList<JobRecord> ListQueue() =>
        QueryRecords("WHERE j.status = 'pending' ORDER BY j.id");

    public IReadOnlyList<JobRecord> ListActive() =>
        QueryRecords("WHERE j.status IN ('pending', 'running') ORDER BY j.id");

    public JobRecord? GetRecord(long id) =>
        QueryRecords("WHERE j.id = $id", ("$id", id)).FirstOrDefault();

    public IReadOnlyList<JobRecord> ListForItem(string repo, int number) =>
        QueryRecords(
            "WHERE j.repo = $repo COLLATE NOCASE AND j.number = $number ORDER BY j.id DESC",
            ("$repo", repo),
            ("$number", number));

    public JobPage QueryHistory(JobQuery query)
    {
        var conditions = new List<string>();
        var args = new List<(string, object?)>();

        if (query.Status is not null)
        {
            conditions.Add("j.status = $status");
            args.Add(("$status", query.Status));
        }

        if (query.Type is not null)
        {
            conditions.Add("j.type = $type");
            args.Add(("$type", query.Type));
        }

        if (query.Repos is not null)
        {
            var names = query.Repos.Select((repo, index) => ($"$repo{index}", repo)).ToList();
            var placeholders = names.Count == 0 ? "NULL" : string.Join(", ", names.Select(name => name.Item1));
            conditions.Add($"j.repo COLLATE NOCASE IN ({placeholders})");
            args.AddRange(names.Select(name => (name.Item1, (object?)name.repo)));
        }

        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        var countSql = $"SELECT COUNT(*) FROM jobs j {where}";
        var total = Convert.ToInt32(Scalar(countSql, args.ToArray()) ?? 0, CultureInfo.InvariantCulture);

        var pageSize = Math.Max(1, query.PageSize);

        // long: a huge page number must give an empty page, not wrap around to a wrong one.
        var offset = (long)(Math.Max(1, query.Page) - 1) * pageSize;
        var items = QueryRecords($"{where} ORDER BY j.id DESC LIMIT {pageSize} OFFSET {offset}", args.ToArray());

        return new JobPage(items, total);
    }

    // ---- helpers ----

    private List<JobRecord> QueryRecords(string tail, params (string, object?)[] args)
    {
        var sql = $"""
            SELECT j.id, j.type, j.repo, j.number, j.status, j.attempts, j.created_at, j.started_at, j.finished_at,
                j.error, j.log_dir, j.result, j.detail, j.copilot_calls
            FROM jobs j {tail}
            """;

        using var connection = Open();
        using var command = Command(connection, sql, args);
        using var reader = command.ExecuteReader();

        var records = new List<JobRecord>();
        while (reader.Read())
        {
            records.Add(new JobRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt32(5),
                ParseTime(reader.GetString(6)),
                reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
                reader.IsDBNull(8) ? null : ParseTime(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetInt32(13)));
        }

        return records;
    }

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Upgrades a database made by an older runner; a new database already has the column.</summary>
    private void AddColumnIfMissing(string table, string column, string type)
    {
        using var connection = Open();
        using var command = Command(connection, $"PRAGMA table_info({table})");
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = Command(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
        alter.ExecuteNonQuery();
    }

    private List<StoredJob> QueryJobs(string where, params (string, object?)[] args)
    {
        using var connection = Open();
        using var command = Command(
            connection,
            $"SELECT id, type, repo, number, payload, status, attempts FROM jobs {where}",
            args);
        using var reader = command.ExecuteReader();

        var jobs = new List<StoredJob>();
        while (reader.Read())
        {
            var typeName = reader.GetString(1);
            var jobType = Enum.Parse<JobType>(typeName);
            var repo = reader.GetString(2);
            var number = reader.GetInt32(3);
            var payload = reader.IsDBNull(4) ? null : reader.GetString(4);
            var request = new JobRequest(jobType, repo, number, payload);
            var id = reader.GetInt64(0);
            var status = reader.GetString(5);
            var attempts = reader.GetInt32(6);
            jobs.Add(new StoredJob(id, request, status, attempts));
        }

        return jobs;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        return connection;
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private void Execute(string sql, params (string, object?)[] args)
    {
        using var connection = Open();
        using var command = Command(connection, sql, args);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var connection = Open();
        using var command = Command(connection, sql, args);
        var value = command.ExecuteScalar();

        return value is DBNull ? null : value;
    }

    private static string Time(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
