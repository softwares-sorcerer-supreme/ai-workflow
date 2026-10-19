using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Web;

/// <summary>One file of a job's log folder.</summary>
public sealed record LogFileInfo(string Name, long SizeBytes);

/// <summary>The content of one log file, as text.</summary>
public sealed record LogFileContent(string Name, long SizeBytes, string Text, bool IsTruncated);

public enum LogReadStatus
{
    Ok,
    BadName,
    NotFound,
}

public sealed record LogReadResult(LogReadStatus Status, LogFileContent? Content = null);

/// <summary>
/// Reads the files of one job's log folder for the dashboard. This is a security boundary (docs/specs/dashboard.md):
/// the folder is <c>logs/jobs/&lt;log_dir&gt;/</c> only, a file is addressed by a plain name that must be one of the
/// files the folder holds (it is matched against the listing, never turned into a path), links are not followed, and
/// nothing is read from any other place.
/// </summary>
public sealed class LogViewer(RunnerConfig config)
{
    private string JobsRoot
    {
        get
        {
            var logsFolder = config.ResolvePath(config.Paths.Logs);
            var jobsFolder = Path.Combine(logsFolder, "jobs");

            return Path.GetFullPath(jobsFolder);
        }
    }

    /// <summary>The files of the job's log folder, by name; empty when there is no folder (yet, or any more).</summary>
    public IReadOnlyList<LogFileInfo> List(JobRecord job)
    {
        var folder = FolderOf(job);
        if (folder is null)
        {
            return [];
        }

        return FilesIn(folder)
            .Select(file => new LogFileInfo(file.Name, file.Length))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public LogReadResult Read(JobRecord job, string? name)
    {
        if (!LogFileRules.IsPlainName(name))
        {
            return new LogReadResult(LogReadStatus.BadName);
        }

        var folder = FolderOf(job);
        if (folder is null)
        {
            return new LogReadResult(LogReadStatus.NotFound);
        }

        var file = FilesIn(folder)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (file is null)
        {
            return new LogReadResult(LogReadStatus.NotFound);
        }

        return ReadText(file);
    }

    /// <summary>The job's folder, or null when it is not a real folder directly under logs/jobs/.</summary>
    private string? FolderOf(JobRecord job)
    {
        // A tampered database row must not point the viewer elsewhere.
        if (!LogFileRules.IsPlainName(job.LogDir))
        {
            return null;
        }

        var root = JobsRoot;
        var folder = Path.GetFullPath(Path.Combine(root, job.LogDir!));
        var parent = Path.GetDirectoryName(folder);
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var info = new DirectoryInfo(folder);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        return folder;
    }

    /// <summary>Regular files directly in the folder; no sub folders, no links.</summary>
    private static List<FileInfo> FilesIn(string folder) =>
        new DirectoryInfo(folder)
            .EnumerateFiles()
            .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .ToList();

    private static LogReadResult ReadText(FileInfo file)
    {
        try
        {
            // The runner may still be appending to job.log: share the file.
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var size = stream.Length;
            var buffer = new byte[Math.Min(size, LogFileRules.MaxBytes)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var isTruncated = size > LogFileRules.MaxBytes;
            var text = Encoding.UTF8.GetString(buffer, 0, read);

            var content = new LogFileContent(file.Name, size, text, isTruncated);

            return new LogReadResult(LogReadStatus.Ok, content);
        }
        catch (IOException)
        {
            // Deleted by the log cleanup between the listing and the read.
            return new LogReadResult(LogReadStatus.NotFound);
        }
    }
}
