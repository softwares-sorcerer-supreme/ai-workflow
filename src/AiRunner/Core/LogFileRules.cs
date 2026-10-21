namespace AiRunner.Core;

/// <summary>
/// What may be read through the dashboard's log viewer. The viewer is a security boundary: a name from the URL must
/// be one plain file name, so no request can name a path outside the job's log folder.
/// </summary>
public static class LogFileRules
{
    /// <summary>Largest part of a file the viewer returns; the rest is cut with a note.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>End of the name of a file the runner appends to while Copilot runs ("round-2-copilot-live.txt").</summary>
    public const string LiveFileSuffix = "-live.txt";

    /// <summary>True for a file that grows while its job runs, so the dashboard tails it.</summary>
    public static bool IsLiveName(string name) => name.EndsWith(LiveFileSuffix, StringComparison.Ordinal);

    private static readonly char[] ForbiddenChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];

    /// <summary>
    /// True for a name that is only a file name: not empty, not "." or "..", no separator, no drive or stream colon,
    /// no wildcard and no control character.
    /// </summary>
    public static bool IsPlainName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name is "." or "..")
        {
            return false;
        }

        if (name.IndexOfAny(ForbiddenChars) >= 0)
        {
            return false;
        }

        return !name.Any(char.IsControl);
    }
}
