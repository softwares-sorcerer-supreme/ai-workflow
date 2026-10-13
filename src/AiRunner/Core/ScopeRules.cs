namespace AiRunner.Core;

/// <summary>A change the runner undid because the agent made it where it may not write.</summary>
/// <param name="Repo">The repo whose clone had the change.</param>
/// <param name="Path">The path from the root of that repo.</param>
public sealed record UndoneChange(string Repo, string Path, string Reason);

/// <summary>Where the agent may write in the clone of the target repo.</summary>
/// <param name="ComponentPaths">Folders (from the repo root) of the components that may change; "." is the whole repo.</param>
/// <param name="NeverEdit">gitignore-like patterns of files that may never change.</param>
public sealed record WriteScope(IReadOnlyList<string> ComponentPaths, IReadOnlyList<string> NeverEdit);

/// <summary>
/// The path rules of the scope guard: a job changes code in one component only, and never in the files
/// <c>never_edit</c> lists. Pure: the git side lists and restores the paths, this class only says which to undo.
/// </summary>
public static class ScopeRules
{
    public const string ReadOnlyReason = "this component is read-only in this job";
    public const string OutsideReason = "outside the target component folder";
    public const string NeverEditReason = "matches never_edit";

    /// <summary>
    /// The changes to undo in one clone. A null scope means a read-only clone: every change goes.
    /// A path in the target component that does not match never_edit stays.
    /// </summary>
    public static IReadOnlyList<UndoneChange> ToUndo(string repo, IEnumerable<string> changedPaths, WriteScope? scope)
    {
        var undone = new List<UndoneChange>();

        foreach (var path in changedPaths)
        {
            var reason = WhyUndo(path, scope);
            if (reason is not null)
            {
                undone.Add(new UndoneChange(repo, path, reason));
            }
        }

        return undone;
    }

    /// <summary>Why a change at this path must be undone, or null when it may stay.</summary>
    private static string? WhyUndo(string path, WriteScope? scope)
    {
        if (scope is null)
        {
            return ReadOnlyReason;
        }

        var isInside = scope.ComponentPaths.Any(componentPath => IsInside(path, componentPath));
        if (!isInside)
        {
            return OutsideReason;
        }

        return MatchesNeverEdit(path, scope.NeverEdit) ? NeverEditReason : null;
    }

    /// <summary>True when the path is the folder itself or below it. "." (or empty) is the whole repo.</summary>
    public static bool IsInside(string path, string componentPath)
    {
        var folder = Normalize(componentPath);
        if (folder is "" or ".")
        {
            return true;
        }

        var normalizedPath = Normalize(path);
        return normalizedPath.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Case-insensitive, gitignore-like, against the path from the repo root. A trailing "/" means a folder with
    /// that name anywhere; no "/" means a file or folder name anywhere; a "/" in the middle (or at the start)
    /// anchors the pattern at the repo root. "*" stays inside one name, "**" crosses folders. A pattern that
    /// matches a folder covers everything below it.
    /// </summary>
    public static bool MatchesNeverEdit(string path, IEnumerable<string> patterns)
    {
        var segments = Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);

        return patterns.Any(pattern => Matches(segments, pattern));
    }

    private static bool Matches(string[] segments, string rawPattern)
    {
        var pattern = rawPattern.Trim().Replace('\\', '/');
        var isFolder = pattern.EndsWith('/');
        var trimmedPattern = pattern.Trim('/');
        if (trimmedPattern.Length == 0)
        {
            return false;
        }

        var isAnchored = pattern.StartsWith('/') || trimmedPattern.Contains('/');

        // A folder pattern cannot match the last segment: that is the file.
        var matchableCount = isFolder ? segments.Length - 1 : segments.Length;

        if (!isAnchored)
        {
            for (var index = 0; index < matchableCount; index++)
            {
                if (PathGlob.IsMatch(segments[index], trimmedPattern))
                {
                    return true;
                }
            }

            return false;
        }

        for (var length = 1; length <= matchableCount; length++)
        {
            var prefix = string.Join('/', segments.Take(length));
            if (PathGlob.IsMatch(prefix, trimmedPattern))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim('/');
    }
}
