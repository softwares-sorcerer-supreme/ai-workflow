using System.Text;
using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>Cuts a unified diff ("git diff") down to some files, so an agent neither reads nor pays for the rest.</summary>
public static class DiffFiles
{
    private static readonly Regex FileHeader = new(@"^diff --git ""?a/(?<old>.+?)""? ""?b/(?<new>.+?)""?$");

    /// <summary>
    /// Only the file sections of <paramref name="diff"/> whose old or new path is in <paramref name="paths"/>. An
    /// incremental review uses it to drop what came in by merging the base branch: those files are not the PR's.
    /// </summary>
    public static string Only(string diff, IReadOnlyCollection<string> paths)
    {
        var kept = new StringBuilder();
        var isKept = false;

        foreach (var line in diff.Split('\n'))
        {
            // Only a header line can start a file section; the regex runs on those alone.
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var header = FileHeader.Match(line.TrimEnd('\r'));
                isKept = header.Success &&
                         (paths.Contains(header.Groups["old"].Value) || paths.Contains(header.Groups["new"].Value));
            }

            if (isKept)
            {
                kept.Append(line).Append('\n');
            }
        }

        return kept.ToString();
    }
}
