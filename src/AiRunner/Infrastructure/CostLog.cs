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
public sealed class CostLog(RunnerConfig config, IStateStore stateStore, ILogger<CostLog> logger) : ICostLog
{
    private const string OldHeader = "date,repo,number,workflow,duration_s,result,detail";
    private const string Header = OldHeader + ",copilot_calls";

    private static readonly object Gate = new();

    public void Record(CostEntry entry)
    {
        try
        {
            stateStore.AddCost(entry);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Could not write the cost row to the database: {Message}", exception.Message);
        }

        var logsFolder = config.ResolvePath(config.Paths.Logs);
        var file = Path.Combine(logsFolder, "cost.csv");

        try
        {
            lock (Gate)
            {
                var csvFolder = Path.GetDirectoryName(file)!;
                Directory.CreateDirectory(csvFolder);
                if (!File.Exists(file))
                {
                    File.WriteAllText(file, Header + "\n");
                }
                else
                {
                    UpgradeHeader(file);
                }

                var timestamp = entry.At.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                var csvRepo = Csv(entry.Repo);
                var duration = entry.DurationSeconds.ToString("F0", CultureInfo.InvariantCulture);
                var csvResult = Csv(entry.Result);
                var csvDetail = Csv(entry.Detail);
                var calls = entry.CopilotCalls?.ToString(CultureInfo.InvariantCulture) ?? "";
                var row = string.Join(
                    ",", timestamp, csvRepo, entry.Number, entry.Workflow, duration, csvResult, csvDetail, calls);
                File.AppendAllText(file, row + "\n");
            }
        }
        catch (IOException exception)
        {
            logger.LogWarning("Could not append to {File} (open in Excel?): {Message}", file, exception.Message);
        }
    }

    /// <summary>
    /// A file from an older runner has the 7-column header. Only that line changes; its rows stay as they are
    /// (a spreadsheet reads the missing last field as empty, which is "-" in the dashboard too).
    /// </summary>
    private static void UpgradeHeader(string file)
    {
        // Only the first line is read on the usual path; the file is rewritten once, for the old header.
        var firstLine = File.ReadLines(file).FirstOrDefault();
        if (firstLine != OldHeader)
        {
            return;
        }

        var lines = File.ReadAllLines(file);
        lines[0] = Header;
        File.WriteAllText(file, string.Join('\n', lines) + "\n");
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"").Replace('\n', ' ').Replace('\r', ' ') + "\"";
}
