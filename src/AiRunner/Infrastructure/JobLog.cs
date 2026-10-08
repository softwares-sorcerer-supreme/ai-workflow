using System.Globalization;
using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>
/// The archive folder of one job run: logs/jobs/{time}-{owner}-{repo}-{n}-{type}/. Jobs save
/// their inputs, the raw agent output and the extracted blocks here for debugging (kept 30 days).
/// The folder is created on the first write, so a job that writes nothing leaves nothing.
/// </summary>
public sealed class JobRun(string dir, ILogger log)
{
    public string Dir { get; } = dir;

    public void Save(string fileName, string content) =>
        File.WriteAllText(Path.Combine(EnsureDir(), fileName), content);

    public void SaveCopy(string sourceFile, string fileName)
    {
        if (File.Exists(sourceFile)) File.Copy(sourceFile, Path.Combine(EnsureDir(), fileName), overwrite: true);
    }

    /// <summary>A line in job.log and in the console.</summary>
    public void Info(string message)
    {
        log.LogInformation("{Message}", message);
        File.AppendAllText(Path.Combine(EnsureDir(), "job.log"),
            $"{DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {message}\n");
    }

    private string EnsureDir() => Directory.CreateDirectory(Dir).FullName;
}

public sealed class JobLog(RunnerConfig config, ILogger<JobLog> log)
{
    private string Root => Path.Combine(config.ResolvePath(config.Paths.Logs), "jobs");

    public JobRun Begin(JobRequest request)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new JobRun(Path.Combine(Root, $"{stamp}-{request.Repo.Replace('/', '-')}-{request.Number}-{request.Type}"), log);
    }

    /// <summary>Deletes run folders older than logRetentionDays.</summary>
    public void Cleanup()
    {
        if (!Directory.Exists(Root)) return;
        var limit = DateTime.Now.AddDays(-config.LogRetentionDays);
        foreach (var dir in Directory.EnumerateDirectories(Root).Where(d => Directory.GetCreationTime(d) < limit))
            if (!FileSystem.DeleteDirectory(dir))
                log.LogWarning("Could not delete old log {Dir}", dir);
    }
}
