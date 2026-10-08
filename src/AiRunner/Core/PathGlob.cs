using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>
/// Minimal glob for review.skipPaths: "**" = any folders, "*" = any name part, "?" = one char.
/// "**/*.md" also matches "README.md" at the root.
/// </summary>
public static class PathGlob
{
    public static bool IsMatch(string path, string glob) => ToRegex(glob).IsMatch(path.Replace('\\', '/'));

    public static bool AllMatch(IReadOnlyCollection<string> paths, IReadOnlyCollection<string> globs) =>
        paths.Count > 0 && globs.Count > 0 && paths.All(p => globs.Any(g => IsMatch(p, g)));

    private static Regex ToRegex(string glob)
    {
        var g = glob.Replace('\\', '/');
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
            {
                var slash = i + 2 < g.Length && g[i + 2] == '/';
                sb.Append(slash ? "(.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase);
    }
}
