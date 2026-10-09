namespace AiRunner.Core;

/// <summary>
/// Copilot output has extra text around the answer. The agent wraps its answer in
/// &lt;!-- NAME_START --&gt; ... &lt;!-- NAME_END --&gt;; we keep only that block.
/// The LAST start marker is used, in case the agent repeats the template first.
/// </summary>
public static class Markers
{
    public const string Analysis = "ANALYSIS";
    public const string Spec = "SPEC";
    public const string Review = "REVIEW";
    public const string ReviewFix = "REVIEW_FIX";

    private static string Start(string name) => $"<!-- {name}_START -->";
    private static string End(string name) => $"<!-- {name}_END -->";

    /// <summary>Text between the markers, trimmed, without the markers. Null if not found.</summary>
    public static string? Extract(string text, string name)
    {
        var start = Start(name);
        var end = End(name);

        var startIndex = text.LastIndexOf(start, StringComparison.Ordinal);

        if (startIndex < 0)
        {
            return null;
        }

        var from = startIndex + start.Length;
        var endIndex = text.IndexOf(end, from, StringComparison.Ordinal);

        return endIndex < 0 ? null : text[from..endIndex].Trim();
    }

    /// <summary>
    /// The block with its markers on their own lines, ready to post. The inner text is agent output that
    /// issue and PR text can steer, so its HTML comments are neutralised: it cannot fake a block marker or
    /// a context-upto tag that the runner would trust later.
    /// </summary>
    public static string Wrap(string blockText, string name) =>
        $"{Start(name)}\n{blockText.Trim().Replace("<!--", "&lt;!--", StringComparison.Ordinal)}\n{End(name)}";

    public static bool Contains(string? text, string name)
    {
        if (text is null)
        {
            return false;
        }

        var start = Start(name);

        return text.Contains(start, StringComparison.Ordinal);
    }
}
