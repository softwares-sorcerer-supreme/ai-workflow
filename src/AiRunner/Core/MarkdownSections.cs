using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>Small helpers to read the fixed-layout Markdown the agents write.</summary>
public static class MarkdownSections
{
    /// <summary>
    /// Text under "### {heading}" up to the next "### " or "## " heading. Null if the heading
    /// is missing. Matches on the heading start, so "### Spec check (only ...)" also matches "Spec check".
    /// </summary>
    public static string? Get(string markdown, string heading)
    {
        var lines = SplitLines(markdown);
        var start = Array.FindIndex(lines, l =>
            l.TrimStart().StartsWith("### ", StringComparison.Ordinal) &&
            l.TrimStart()[4..].Trim().StartsWith(heading, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;

        var body = lines.Skip(start + 1)
            .TakeWhile(l => !l.TrimStart().StartsWith("### ", StringComparison.Ordinal) &&
                            !l.TrimStart().StartsWith("## ", StringComparison.Ordinal));
        return string.Join("\n", body).Trim();
    }

    /// <summary>True for an empty section or one that only says None.</summary>
    public static bool IsNone(string? section) =>
        string.IsNullOrWhiteSpace(section) ||
        section.Trim().TrimStart('-', '*', ' ').TrimEnd('.').Equals("None", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Data rows of the first Markdown table in the text: header and separator rows are skipped,
    /// cells are trimmed. Rows outside the table are ignored.
    /// </summary>
    public static List<string[]> TableRows(string text)
    {
        var rows = new List<string[]>();
        var seenSeparator = false;
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (!line.StartsWith('|'))
            {
                if (seenSeparator && rows.Count > 0) break;  // table ended
                continue;
            }
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
            {
                seenSeparator = true;
                continue;
            }
            if (!seenSeparator) continue;                     // header row
            rows.Add(cells);
        }
        return rows;
    }

    /// <summary>Value of a "**Name:** value" line, lower case. Throws if missing.</summary>
    public static string ReadField(string block, string name)
    {
        var m = Regex.Match(block, $@"^[\s*_>-]*{name}[\s*_]*:[\s*_]*(?<v>[^\r\n]*?)[\s*_]*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!m.Success)
            throw new FormatException($"'{name}:' line not found in the block");
        return m.Groups["v"].Value.Trim().ToLowerInvariant();
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');
}
