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

    public static bool IsBot(GbComment comment) => comment.Body?.Contains(Marker, StringComparison.Ordinal) == true;

    /// <summary>A bot comment that holds a block of that kind (Markers.Analysis, Spec, Review).</summary>
    public static bool Is(GbComment comment, string marker) =>
        IsBot(comment) && Markers.Contains(comment.Body, marker);

    public static bool IsAnalysis(GbComment comment) => Is(comment, Markers.Analysis);

    /// <summary>The newest bot comment with a block of that kind, or null.</summary>
    public static GbComment? Latest(IEnumerable<GbComment> comments, string marker) =>
        comments.Where(comment => Is(comment, marker)).MaxBy(comment => comment.Id);

    /// <summary>The people's comments (no bot comments), oldest first.</summary>
    public static IEnumerable<GbComment> Humans(IEnumerable<GbComment> comments) =>
        comments
            .Where(comment => !IsBot(comment))
            .OrderBy(comment => comment.Id);

    public static bool MentionsAi(GbComment comment) =>
        !IsBot(comment) && comment.Body is not null &&
        Regex.IsMatch(comment.Body, @"(^|[^\w])@ai\b", RegexOptions.IgnoreCase);

    public static int Count(IEnumerable<GbComment> comments, string marker) =>
        comments.Count(comment => Is(comment, marker));
}
