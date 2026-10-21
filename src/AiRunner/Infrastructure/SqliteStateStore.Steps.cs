namespace AiRunner.Infrastructure;

/// <summary>The step log of the jobs: a table like <c>jobs</c>, safe to delete with the rest of the database.</summary>
public sealed partial class SqliteStateStore
{
    private void CreateStepTables() =>
        Execute("""
            CREATE TABLE IF NOT EXISTS job_step (job_id INTEGER NOT NULL, seq INTEGER NOT NULL, code TEXT NOT NULL,
                label TEXT NOT NULL, started_at TEXT NOT NULL, ended_at TEXT, outcome TEXT, detail TEXT,
                PRIMARY KEY (job_id, seq));
            """);

    public void ClearSteps(long jobId) =>
        Execute("DELETE FROM job_step WHERE job_id = $job", ("$job", jobId));

    public void OpenStep(JobStepRecord step) =>
        Execute(
            """
            INSERT OR REPLACE INTO job_step (job_id, seq, code, label, started_at)
            VALUES ($job, $seq, $code, $label, $started)
            """,
            ("$job", step.JobId),
            ("$seq", step.Seq),
            ("$code", step.Code),
            ("$label", step.Label),
            ("$started", Time(step.StartedAt)));

    public void CloseStep(JobStepEnd end) =>
        Execute(
            """
            UPDATE job_step SET ended_at = $ended, outcome = $outcome, detail = $detail
            WHERE job_id = $job AND seq = $seq
            """,
            ("$job", end.JobId),
            ("$seq", end.Seq),
            ("$ended", Time(end.EndedAt)),
            ("$outcome", end.Outcome),
            ("$detail", end.Detail));

    public IReadOnlyList<JobStepRecord> ListSteps(long jobId)
    {
        using var connection = Open();
        using var command = Command(
            connection,
            """
            SELECT seq, code, label, started_at, ended_at, outcome, detail
            FROM job_step WHERE job_id = $job ORDER BY seq
            """,
            ("$job", jobId));
        using var reader = command.ExecuteReader();

        var steps = new List<JobStepRecord>();
        while (reader.Read())
        {
            steps.Add(new JobStepRecord(
                jobId,
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseTime(reader.GetString(3)),
                reader.IsDBNull(4) ? null : ParseTime(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return steps;
    }
}
