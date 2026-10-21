using System.Globalization;
using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>
/// The archive folder of one job run: logs/jobs/{time}-{owner}-{repo}-{n}-{type}/. Jobs save
/// their inputs, the raw agent output and the extracted blocks here for debugging (kept 30 days).
/// The folder is created on the first write, so a job that writes nothing leaves nothing.
/// </summary>
public sealed class JobRun(string directory, ILogger logger)
{
    /// <summary>
    /// End of the name of every Copilot usage file in the folder ("copilot-usage.json", "round-2-copilot-usage.json").
    /// </summary>
    public const string UsageFileSuffix = "-usage.json";

    public string Dir { get; } = directory;

    /// <summary>When JobWorker cancels the job (it sets this from the same timeout); jobs use it to avoid starting work they cannot finish.</summary>
    public DateTimeOffset Deadline { get; set; } = DateTimeOffset.MaxValue;

    public TimeSpan TimeLeft => Deadline - DateTimeOffset.UtcNow;

    /// <summary>
    /// Set by JobWorker for a routine job when claims are on. The job calls <see cref="ClaimAsync"/> just before its
    /// first write; without a claimer (claims off, or a test) the answer is always yes.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? Claimer { get; set; }

    /// <summary>True when the job may write: this runner holds the claim, or claims are off.</summary>
    public Task<bool> ClaimAsync(CancellationToken cancellationToken) =>
        Claimer is null ? Task.FromResult(true) : Claimer(cancellationToken);

    /// <summary>
    /// Set by JobWorker so the dashboard can show which round an agent session is in. Without it (tests, other
    /// modes) reporting a round does nothing.
    /// </summary>
    public Action<int>? RoundChanged { get; set; }

    public void ReportRound(int round) => RoundChanged?.Invoke(round);

    public void Save(string fileName, string content)
    {
        var directoryPath = EnsureDir();
        var targetFile = Path.Combine(directoryPath, fileName);
        File.WriteAllText(targetFile, content);
    }

    public void SaveCopy(string sourceFile, string fileName)
    {
        if (File.Exists(sourceFile))
        {
            var directoryPath = EnsureDir();
            var targetFile = Path.Combine(directoryPath, fileName);
            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    /// <summary>The full path of a file in the folder, for a tool that writes it itself (the folder is made).</summary>
    public string PathFor(string fileName)
    {
        var directoryPath = EnsureDir();

        return Path.Combine(directoryPath, fileName);
    }

    /// <summary>
    /// The sum of every Copilot usage file of the run, or null when there is none (no Copilot call, or a CLI without
    /// --usage-output-file). A file that cannot be read is skipped: cost is best effort, it never fails a job.
    /// </summary>
    public JobUsage? ReadUsage()
    {
        if (!Directory.Exists(Dir))
        {
            return null;
        }

        var usages = new List<JobUsage>();
        var usageFiles = Directory
            .EnumerateFiles(Dir, "*" + UsageFileSuffix)
            .Order(StringComparer.Ordinal);
        foreach (var usageFile in usageFiles)
        {
            // The run is the file name without the suffix: "copilot", "round-2-copilot".
            var fileName = Path.GetFileName(usageFile);
            var runName = fileName[..^UsageFileSuffix.Length];
            JobUsage? usage;
            try
            {
                var json = File.ReadAllText(usageFile);
                usage = CopilotUsage.Parse(json, runName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not read {File}: {Message}", usageFile, exception.Message);
                continue;
            }

            if (usage is not null)
            {
                usages.Add(usage);
            }
        }

        return usages.Count == 0 ? null : JobUsage.Combine(usages);
    }

    /// <summary>A line in job.log and in the console.</summary>
    public void Info(string message)
    {
        logger.LogInformation("{Message}", message);
        var directoryPath = EnsureDir();
        var logFile = Path.Combine(directoryPath, "job.log");
        File.AppendAllText(
            logFile,
            $"{DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {message}\n");
    }

    private string EnsureDir() => Directory.CreateDirectory(Dir).FullName;
}

public sealed class JobLog(RunnerConfig config, ILogger<JobLog> logger)
{
    private string Root => config.JobLogsFolder;

    public JobRun Begin(JobRequest request)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var folderName = $"{stamp}-{request.Repo.Replace('/', '-')}-{request.Number}-{request.Type}";

        var runFolder = Path.Combine(Root, folderName);

        return new JobRun(runFolder, logger);
    }

    /// <summary>Deletes run folders older than logRetentionDays.</summary>
    public void Cleanup()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var limit = DateTime.Now.AddDays(-config.LogRetentionDays);
        var oldFolders = Directory.EnumerateDirectories(Root)
            .Where(folder => Directory.GetCreationTime(folder) < limit);

        foreach (var oldFolder in oldFolders)
        {
            if (!FileSystem.DeleteDirectory(oldFolder))
            {
                logger.LogWarning("Could not delete old log {Dir}", oldFolder);
            }
        }
    }
}
