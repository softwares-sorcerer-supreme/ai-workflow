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
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
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
}
