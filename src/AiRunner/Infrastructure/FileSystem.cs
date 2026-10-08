namespace AiRunner.Infrastructure;

public static class FileSystem
{
    /// <summary>
    /// Deletes a folder and everything in it, also read-only files (git objects are read-only
    /// on Windows). Returns false when it could not; the caller decides whether that matters.
    /// </summary>
    public static bool DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return true;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
