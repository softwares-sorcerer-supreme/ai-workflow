using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>The block kind of the replies and the word that asks for the next one (the project's trigger_keyword).</summary>
public sealed record ThreadKind(string Marker, string Keyword = "@ai");

/// <summary>
/// The bot replies of one block kind (Markers.Analysis, Markers.ReviewFix) on one issue or PR, and the
/// human comments around them: which @ai comment is new, how many replies exist, what a new reply
/// records. Pure: the caller passes the comments and the local cursor in.
/// A reply remembers the newest comment id its job had read (read-up-to). A comment written while
/// Copilot ran has an id below the reply's own id but above that tag, so it still counts as new, even
/// without the database. The local cursor (SQLite) only covers a job that failed before it could reply.
/// </summary>
public sealed class AtAiThread
{
    private const string UptoPrefix = "<!-- ai-runner:context-upto=";

    private readonly IReadOnlyList<HostComment> _comments;
    private readonly string _marker;
    private readonly string _keyword;
    private readonly long _replyCursor;

    private AtAiThread(IReadOnlyList<HostComment> comments, ThreadKind kind, long localCursor)
    {
        _comments = comments;
        _marker = kind.Marker;
        _keyword = kind.Keyword;

        var latestReply = BotComments.Latest(comments, _marker);
        _replyCursor = latestReply is null ? 0 : ReadUpToOf(latestReply) ?? latestReply.Id;
        AnsweredUpTo = Math.Max(localCursor, _replyCursor);
        ReadUpTo = comments.Count == 0 ? 0 : comments.Max(comment => comment.Id);
        Done = BotComments.Count(comments, _marker);
    }

    /// <param name="marker">The block kind of the replies: Markers.Analysis or Markers.ReviewFix.</param>
    /// <param name="localCursor">
    /// Newest @ai comment the local state says was handled (0 = none). Only <see cref="Trigger"/> and
    /// <see cref="AnsweredUpTo"/> read it.
    /// </param>
    public static AtAiThread Of(IReadOnlyList<HostComment> comments, string marker, long localCursor) =>
        new(comments, new ThreadKind(marker), localCursor);

    /// <summary>Same, for a project with its own trigger keyword.</summary>
    public static AtAiThread Of(IReadOnlyList<HostComment> comments, ThreadKind kind, long localCursor) =>
        new(comments, kind, localCursor);

    /// <summary>Number of bot replies of this kind so far.</summary>
    public int Done { get; }

    /// <summary>Newest comment id seen: what the next reply records, and what the job stores as handled.</summary>
    public long ReadUpTo { get; }

    /// <summary>Comments after this id are new: the later of the last reply's read-up-to and the local cursor.</summary>
    public long AnsweredUpTo { get; }

    /// <summary>The newest human @ai comment after <see cref="AnsweredUpTo"/>, or null.</summary>
    public HostComment? Trigger =>
        _comments
            .Where(comment => comment.Id > AnsweredUpTo && BotComments.MentionsAi(comment, _keyword))
            .MaxBy(comment => comment.Id);

    /// <summary>
    /// Comments after the last reply, ignoring the local cursor on purpose: a failed job marks its @ai comment
    /// handled, but its requests were never applied and must reach the next one.
    /// </summary>
    public IEnumerable<HostComment> RequestsSinceReply => _comments.Where(comment => comment.Id > _replyCursor);

    /// <summary>The number of the next reply, and whether it is over the limit.</summary>
    public (int Number, bool LimitReached) Next(int limit) => (Done + 1, Done >= limit);

    /// <summary>The block plus the read-up-to tag, ready to post.</summary>
    public string Reply(string blockText) => ReplyBody(blockText, _marker, ReadUpTo);

    /// <summary>Same as <see cref="Reply"/> for a read-up-to the caller already has.</summary>
    public static string ReplyBody(string blockText, string marker, long readUpTo) =>
        Markers.Wrap(blockText, marker) + "\n" + $"{UptoPrefix}{readUpTo} -->";

    /// <summary>The read-up-to a reply recorded, or null (a reply from before the tag existed).</summary>
    public static long? ReadUpToOf(HostComment reply)
    {
        // The last tag: the runner appends it after the text it posts, which could contain a fake one.
        var tagPattern = Regex.Escape(UptoPrefix) + @"(\d+) -->";
        var match = Regex.Matches(reply.Body ?? "", tagPattern).LastOrDefault();

        return match is null ? null : long.Parse(match.Groups[1].Value);
    }
}
