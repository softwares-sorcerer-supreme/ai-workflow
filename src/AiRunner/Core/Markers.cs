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

    private static string Start(string name) => $"<!-- {name}_START -->";
    private static string End(string name) => $"<!-- {name}_END -->";

    /// <summary>Text between the markers, trimmed, without the markers. Null if not found.</summary>
    public static string? Extract(string text, string name)
    {
        var start = Start(name);
        var end = End(name);

        var s = text.LastIndexOf(start, StringComparison.Ordinal);
        if (s < 0) return null;
        var from = s + start.Length;
        var e = text.IndexOf(end, from, StringComparison.Ordinal);
        return e < 0 ? null : text[from..e].Trim();
    }

    /// <summary>The block with its markers on their own lines, ready to post.</summary>
    public static string Wrap(string inner, string name) => $"{Start(name)}\n{inner.Trim()}\n{End(name)}";

    public static bool Contains(string? text, string name) =>
        text is not null && text.Contains(Start(name), StringComparison.Ordinal);
}
