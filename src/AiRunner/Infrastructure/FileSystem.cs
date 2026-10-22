namespace AiRunner.Infrastructure;

public static class FileSystem
{
    /// <summary>
    /// Deletes a folder and everything in it, also read-only files (git objects are read-only
    /// on Windows). Returns false when it could not; the caller decides whether that matters.
    /// </summary>
    public static bool DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return true;
        }

        try
        {
            // Links are skipped: a junction inside the folder must not lead this loop to files outside it.
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when the folder is a link (symlink or junction), which must never be walked or deleted through.</summary>
    public static bool IsLink(string directory) =>
        new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// Clears the read-only attribute of every folder and file under a clone, except its .git folder, so that
    /// git can remove them. Best effort: an entry that cannot be changed is skipped.
    /// </summary>
    public static void ClearReadOnly(string directory)
    {
        var gitFolder = Path.Combine(directory, ".git") + Path.DirectorySeparatorChar;

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            if (entry.StartsWith(gitFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Skipped; git clean reports it if it still matters.
            }
        }
    }

    /// <summary>Deletes a file if it is there; a file that cannot be deleted is left (a temp download, nothing depends on it).</summary>
    public static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next job's git clean removes it.
        }
    }

    /// <summary>A temp file beside the target, then one move over it: readers see the old file or the new.</summary>
    public static void ReplaceFile(string path, string text)
    {
        var folder = Path.GetDirectoryName(path)!;
        var tempFile = Path.Combine(folder, $"{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempFile, text);
            File.Move(tempFile, path, overwrite: true);
        }
        finally
        {
            DeleteFile(tempFile);
        }
    }
}
