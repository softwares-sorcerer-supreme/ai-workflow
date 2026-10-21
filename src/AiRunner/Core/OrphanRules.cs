namespace AiRunner.Core;

/// <summary>
/// Which folder names under work/ the dashboard may delete as an orphan (a security boundary: a request on 127.0.0.1
/// names the folder). Only a plain folder name passes: no path, no drive, no dot names, never a project of the config.
/// The caller still checks that the resolved path stays inside work/.
/// </summary>
public static class OrphanRules
{
    /// <summary>The reason the name is refused, or null when it is a plain folder name of no project.</summary>
    public static string? WhyRefused(string? folder, IEnumerable<string> projectNames)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "The folder name is empty.";
        }

        if (folder is "." or "..")
        {
            return "A folder name must not be '.' or '..'.";
        }

        if (folder.StartsWith('.'))
        {
            return "Folders that start with a dot belong to the runner and are never deleted here.";
        }

        if (folder.EndsWith('.') || folder.EndsWith(' '))
        {
            return "A folder name must not end with a dot or a space.";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var isPlain = folder.IndexOfAny(['/', '\\', ':']) < 0 &&
                      folder.IndexOfAny(invalidChars) < 0 &&
                      !Path.IsPathRooted(folder);
        if (!isPlain)
        {
            return "Only a plain folder name is allowed, not a path.";
        }

        var isProject = projectNames.Contains(folder, StringComparer.OrdinalIgnoreCase);
        if (isProject)
        {
            return "That folder belongs to a project of repos.yml; use re-clone on its clones instead.";
        }

        return null;
    }
}
