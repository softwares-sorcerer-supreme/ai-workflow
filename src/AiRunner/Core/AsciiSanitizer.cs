using System.Text;

namespace AiRunner.Core;

/// <summary>
/// This GitBucket returns HTTP 500 when a comment has any non-ASCII character.
/// Clean() keeps the text readable: common symbols become ASCII, Vietnamese loses
/// its accents ("tieng Viet"), emoji and anything else are dropped.
/// </summary>
public static class AsciiSanitizer
{
    private static readonly Dictionary<char, string> Map = new()
    {
        ['\u2013'] = "-",   // en dash
        ['\u2014'] = "-",   // em dash
        ['\u2018'] = "'",
        ['\u2019'] = "'",
        ['\u201C'] = "\"",
        ['\u201D'] = "\"",
        ['\u2026'] = "...",
        ['\u2022'] = "-",   // bullet
        ['\u2192'] = "->",
        ['\u2190'] = "<-",
        ['\u00D7'] = "x",
        ['\u00A0'] = " ",   // non-breaking space
        ['\u0111'] = "d",   // đ
        ['\u0110'] = "D",   // Đ
    };

    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (ch < 128) { sb.Append(ch); continue; }
            if (Map.TryGetValue(ch, out var replacement)) { sb.Append(replacement); continue; }
            // Everything else is dropped: accent marks (after FormD they are separate chars,
            // so "tiếng" becomes "tieng"), emoji (surrogate pairs) and other symbols.
        }
        return sb.ToString();
    }
}
