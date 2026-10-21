using System.Globalization;
using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

/// <summary>
/// Writes repos.yml for the Settings screen (<see cref="RunnerConfigSource.TryReplace"/> calls it under its lock).
/// A write first copies the old file to <c>repos.yml.bak-&lt;UTC time&gt;</c> (the last 10 are kept), then writes a
/// temp file next to it and moves it over the old one, so a reader never sees half a file.
/// </summary>
public sealed class ConfigFileStore(TimeProvider time)
{
    private const int BackupsKept = 10;

    public void Write(string path, string text)
    {
        var folder = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        var stamp = time
            .GetUtcNow()
            .ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(folder, $"{fileName}.bak-{stamp}");
        File.Copy(path, backupPath, overwrite: true);

        var tempPath = Path.Combine(folder, $"{fileName}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempPath, text);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        PruneBackups(folder, fileName);
    }

    /// <summary>The stamp sorts by time, so the oldest names come first.</summary>
    private static void PruneBackups(string folder, string fileName)
    {
        var oldBackups = Directory
            .GetFiles(folder, $"{fileName}.bak-*")
            .Order(StringComparer.Ordinal)
            .SkipLast(BackupsKept)
            .ToList();

        foreach (var backup in oldBackups)
        {
            File.Delete(backup);
        }
    }
}
