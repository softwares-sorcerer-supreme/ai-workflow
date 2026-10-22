using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>
/// Sets the <c>model:</c> line in the front matter of an agent file's text. <c>AgentInstaller</c> uses it on the
/// installed copy only, so the source file in the repo stays as its author wrote it.
/// </summary>
public static partial class AgentFrontmatter
{
    [GeneratedRegex(@"^model:.*$", RegexOptions.Multiline)]
    private static partial Regex ModelLine();

    /// <summary>
    /// The text with <c>model: X</c> in its front matter; a text without front matter comes back as it is.
    /// </summary>
    public static string WithModel(string text, string model)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newline).ToList();

        if (lines.Count == 0 || lines[0].TrimEnd() != "---")
        {
            return text;
        }

        var closingIndex = lines.FindIndex(1, line => line.TrimEnd() == "---");
        if (closingIndex < 0)
        {
            return text;
        }

        var modelIndex = lines.FindIndex(1, closingIndex - 1, line => ModelLine().IsMatch(line));
        if (modelIndex >= 0)
        {
            lines[modelIndex] = $"model: {model}";
        }
        else
        {
            lines.Insert(closingIndex, $"model: {model}");
        }

        return string.Join(newline, lines);
    }
}
