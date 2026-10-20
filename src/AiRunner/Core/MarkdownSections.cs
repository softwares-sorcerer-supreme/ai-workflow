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
        var headingIndex = Array.FindIndex(lines, line => IsHeading(line, heading));

        if (headingIndex < 0)
        {
            return null;
        }

        var body = lines
            .Skip(headingIndex + 1)
            .TakeWhile(line => !IsSectionEnd(line));

        return string.Join("\n", body).Trim();
    }

    /// <summary>
    /// The text without the section "### {heading}" (heading line and body, up to the next "### " or "## "). Matches
    /// the heading like <see cref="Get"/>; without such a section the text comes back unchanged.
    /// </summary>
    public static string Without(string markdown, string heading)
    {
        var lines = SplitLines(markdown);
        var headingIndex = Array.FindIndex(lines, line => IsHeading(line, heading));

        if (headingIndex < 0)
        {
            return markdown;
        }

        var sectionEnd = Array.FindIndex(lines, headingIndex + 1, IsSectionEnd);
        var kept = sectionEnd < 0
            ? lines.Take(headingIndex)
            : lines.Take(headingIndex).Concat(lines.Skip(sectionEnd));

        return string.Join("\n", kept).TrimEnd();
    }

    private static bool IsHeading(string line, string heading) =>
        line.TrimStart().StartsWith("### ", StringComparison.Ordinal) &&
        line.TrimStart()[4..].Trim().StartsWith(heading, StringComparison.OrdinalIgnoreCase);

    private static bool IsSectionEnd(string line) =>
        line.TrimStart().StartsWith("### ", StringComparison.Ordinal) ||
        line.TrimStart().StartsWith("## ", StringComparison.Ordinal);

    private static readonly Regex AnyHeading = new(@"^[ \t]*#{2,4}[ \t]+", RegexOptions.Multiline);

    /// <summary>
    /// "## X" and "#### X" become "### X". Only for files with one heading level (PR_BODY.md):
    /// agents slip on the level, and that is not worth a fix round.
    /// </summary>
    public static string HeadingsToLevel3(string markdown) => AnyHeading.Replace(markdown, "### ");

    /// <summary>
    /// A missing heading, or an empty one unless it is in mayBeNone. Expects HeadingsToLevel3 text.
    /// The wording goes back to the agent in a fix round.
    /// </summary>
    public static List<string> HeadingProblems(
        string markdown,
        IEnumerable<string> headings,
        params string[] mayBeNone)
    {
        var problems = new List<string>();

        foreach (var heading in headings)
        {
            var section = Get(markdown, heading);

            if (section is null)
            {
                problems.Add($"The heading `### {heading}` is missing.");
            }
            else if (!mayBeNone.Contains(heading) && IsNone(section))
            {
                problems.Add($"`### {heading}` is empty.");
            }
        }

        return problems;
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
        var hasSeenSeparator = false;

        foreach (var rawLine in SplitLines(text))
        {
            var line = rawLine.Trim();

            if (!line.StartsWith('|'))
            {
                if (hasSeenSeparator && rows.Count > 0)
                {
                    break;  // table ended
                }

                continue;
            }

            var cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

            if (cells.All(cell => cell.Length > 0 && cell.All(character => character is '-' or ':')))
            {
                hasSeenSeparator = true;
                continue;
            }

            if (!hasSeenSeparator)
            {
                continue;                     // header row
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>Value of a "**Name:** value" line, lower case. Throws if missing.</summary>
    public static string ReadField(string block, string name)
    {
        var match = Regex.Match(
            block,
            $@"^[\s*_>-]*{name}[\s*_]*:[\s*_]*(?<v>[^\r\n]*?)[\s*_]*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        if (match.Success)
        {
            return match.Groups["v"].Value.Trim().ToLowerInvariant();
        }

        // Fallback: "### Status" heading with the value on the next non-empty line.
        var value = Get(block, name)?
            .Split('\n')
            .Select(line => line.Trim().Trim('*', '_', '`', '-', ' '))
            .FirstOrDefault(line => line.Length > 0);

        return value?.ToLowerInvariant()
            ?? throw new FormatException($"'{name}:' line not found in the block");
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');
}
