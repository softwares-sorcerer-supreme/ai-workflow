using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>
/// The runner posts with the developer's token, so it marks its own comments with a
/// hidden line and never reacts to them. The block markers stay in the posted text,
/// so the kind of a bot comment (analysis, SPEC, review) is found again without a database.
/// Which @ai comment is new, and what a reply records, is <see cref="AtAiThread"/>'s.
/// </summary>
public static class BotComments
{
    public const string Marker = "<!-- ai-runner -->";

    /// <summary>Visible first line, so nobody mistakes a bot comment for a colleague's opinion.</summary>
    public const string VisibleTag = "**[ai-runner]**";

    public static bool IsBot(HostComment comment) => comment.Body?.Contains(Marker, StringComparison.Ordinal) == true;

    /// <summary>A bot comment that holds a block of that kind (Markers.Analysis, Spec, Review).</summary>
    public static bool Is(HostComment comment, string marker) =>
        IsBot(comment) && Markers.Contains(comment.Body, marker);

    public static bool IsAnalysis(HostComment comment) => Is(comment, Markers.Analysis);

    /// <summary>The newest bot comment with a block of that kind, or null.</summary>
    public static HostComment? Latest(IEnumerable<HostComment> comments, string marker) =>
        comments.Where(comment => Is(comment, marker)).MaxBy(comment => comment.Id);

    /// <summary>The people's comments (no bot comments), oldest first.</summary>
    public static IEnumerable<HostComment> Humans(IEnumerable<HostComment> comments) =>
        comments
            .Where(comment => !IsBot(comment))
            .OrderBy(comment => comment.Id);

    public static bool MentionsAi(HostComment comment) => MentionsAi(comment, "@ai");

    /// <summary>A people's comment that contains the project's trigger keyword as a word of its own.</summary>
    public static bool MentionsAi(HostComment comment, string keyword) =>
        !IsBot(comment) && comment.Body is not null &&
        Regex.IsMatch(comment.Body, @"(^|[^\w])" + Regex.Escape(keyword) + @"(?!\w)", RegexOptions.IgnoreCase);

    public static int Count(IEnumerable<HostComment> comments, string marker) =>
        comments.Count(comment => Is(comment, marker));
}
