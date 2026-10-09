using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>
/// Minimal glob for review.skipPaths: "**" = any folders, "*" = any name part, "?" = one char.
/// "**/*.md" also matches "README.md" at the root.
/// </summary>
public static class PathGlob
{
    public static bool IsMatch(string path, string glob)
    {
        var regex = ToRegex(glob);
        var normalizedPath = path.Replace('\\', '/');
        return regex.IsMatch(normalizedPath);
    }

    public static bool AllMatch(IReadOnlyCollection<string> paths, IReadOnlyCollection<string> globs) =>
        paths.Count > 0 && globs.Count > 0 && paths.All(path => globs.Any(glob => IsMatch(path, glob)));

    private static Regex ToRegex(string glob)
    {
        var normalizedGlob = glob.Replace('\\', '/');
        var pattern = new System.Text.StringBuilder("^");

        for (var index = 0; index < normalizedGlob.Length; index++)
        {
            var character = normalizedGlob[index];

            if (character == '*' && index + 1 < normalizedGlob.Length && normalizedGlob[index + 1] == '*')
            {
                var hasSlash = index + 2 < normalizedGlob.Length && normalizedGlob[index + 2] == '/';
                pattern.Append(hasSlash ? "(.*/)?" : ".*");
                index += hasSlash ? 2 : 1;
            }
            else if (character == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (character == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                var characterText = character.ToString();
                var escapedCharacter = Regex.Escape(characterText);
                pattern.Append(escapedCharacter);
            }
        }

        var finishedPattern = pattern.Append('$').ToString();
        return new Regex(finishedPattern, RegexOptions.IgnoreCase);
    }
}
