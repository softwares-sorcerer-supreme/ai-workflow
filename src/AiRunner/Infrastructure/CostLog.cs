using System.Globalization;
using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

public interface ICostLog
{
    /// <summary>Best effort: never throws, so a locked file cannot turn a good job into a failure.</summary>
    void Record(CostEntry entry);
}

/// <summary>
/// One row per job (date, repo, issue, workflow, duration, result) in SQLite and in
/// logs/cost.csv, so premium request use can be read per repo and workflow after a month.
/// The CSV is often open in Excel, which locks it; then the row is only in SQLite.
/// </summary>
public sealed class CostLog(RunnerConfig config, IStateStore state, ILogger<CostLog> log) : ICostLog
{
    private static readonly object Gate = new();

    public void Record(CostEntry e)
    {
        try { state.AddCost(e); }
        catch (Exception ex) { log.LogWarning("Could not write the cost row to the database: {Message}", ex.Message); }

        var file = Path.Combine(config.ResolvePath(config.Paths.Logs), "cost.csv");
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (!File.Exists(file))
                    File.WriteAllText(file, "date,repo,number,workflow,duration_s,result,detail\n");
                File.AppendAllText(file, string.Join(",",
                    e.At.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    Csv(e.Repo), e.Number, e.Workflow,
                    e.DurationSeconds.ToString("F0", CultureInfo.InvariantCulture),
                    Csv(e.Result), Csv(e.Detail)) + "\n");
            }
        }
        catch (IOException ex)
        {
            log.LogWarning("Could not append to {File} (open in Excel?): {Message}", file, ex.Message);
        }
    }

    private static string Csv(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) < 0 ? s : "\"" + s.Replace("\"", "\"\"").Replace('\n', ' ').Replace('\r', ' ') + "\"";
}
