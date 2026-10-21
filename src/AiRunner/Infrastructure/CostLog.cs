using System.Globalization;
using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Infrastructure;

public interface ICostLog
{
    /// <summary>Best effort: never throws, so a locked file cannot turn a good job into a failure.</summary>
    void Record(CostEntry entry);
}

/// <summary>
/// One row per job (date, repo, issue, workflow, duration, result, Copilot calls, tokens and credits) in SQLite and in
/// logs/cost.csv, so Copilot use can be read per repo and workflow after a month.
/// The CSV is often open in Excel, which locks it; then the row is only in SQLite.
/// </summary>
public sealed class CostLog(RunnerConfig config, IStateStore stateStore, ILogger<CostLog> logger) : ICostLog
{
    private const string OldHeader = "date,repo,number,workflow,duration_s,result,detail";
    private const string CallsHeader = OldHeader + ",copilot_calls";
    private static readonly string Header = CallsHeader + "," + string.Join(",", CopilotUsage.ColumnNames);

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
                var usage = UsageFields(entry.Usage?.Total);
                var row = string.Join(
                    ",",
                    timestamp,
                    csvRepo,
                    entry.Number,
                    entry.Workflow,
                    duration,
                    csvResult,
                    csvDetail,
                    calls,
                    usage);
                File.AppendAllText(file, row + "\n");
            }
        }
        catch (IOException exception)
        {
            logger.LogWarning("Could not append to {File} (open in Excel?): {Message}", file, exception.Message);
        }
    }

    /// <summary>The usage fields, empty when the job has no usage (an old CLI, no Copilot call).</summary>
    private static string UsageFields(CopilotUsage? usage)
    {
        if (usage is null)
        {
            return new string(',', CopilotUsage.ColumnNames.Count - 1);
        }

        var values = usage
            .Values()
            .Select(value => value.ToString(CultureInfo.InvariantCulture));

        return string.Join(",", values);
    }

    /// <summary>
    /// A file from an older runner has the 7- or 8-column header. Only that line changes; its rows stay as they are
    /// (a spreadsheet reads the missing last fields as empty, which is "-" in the dashboard too).
    /// </summary>
    private static void UpgradeHeader(string file)
    {
        // Only the first line is read on the usual path; the file is rewritten once, for an old header.
        var firstLine = File.ReadLines(file).FirstOrDefault();
        if (firstLine != OldHeader && firstLine != CallsHeader)
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
