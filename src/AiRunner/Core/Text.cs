namespace AiRunner.Core;

/// <summary>Shortening for log lines, comments and error messages.</summary>
public static class Text
{
    public static string Short(string text, int maxLength = 300)
    {
        text = text.Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    public static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
