using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>
/// Text written by an agent goes into a PR body. GitBucket closes the issue "#n" of the PR's own repo when the PR is
/// merged and the text says "Closes #n". In a project the issue is in another repo, so such a line would close an
/// unrelated issue that happens to have that number. The runner closes issues itself, so the keyword is not needed.
/// </summary>
public static class CloseKeywords
{
    private static readonly Regex CloseReference = new(
        @"(?<![\w`])(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)(:?[ \t]+)((?:[\w.-]+/[\w.-]+)?#\d+)",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Replaces a closing keyword in front of an issue reference ("Closes #5", "fixes: org/repo#5") with "Refs",
    /// so the reference stays readable but closes nothing.
    /// </summary>
    public static string Neutralize(string text) => CloseReference.Replace(text, "Refs$1$2");
}
