namespace AiRunner.Core;

/// <summary>A text that may link attachments, and where a reader should say it came from.</summary>
/// <param name="Label">"the issue body", "comment 1004 by alice".</param>
public sealed record AttachmentSource(string Label, string? Text)
{
    /// <summary>The body first, then one source per human comment; bot comments are skipped, the runner wrote them itself.</summary>
    public static IReadOnlyList<AttachmentSource> Of(AttachmentSource body, IEnumerable<HostComment> comments)
    {
        var commentSources = BotComments.Humans(comments)
            .Select(comment => new AttachmentSource($"comment {comment.Id} by {comment.User.Login}", comment.Body));

        return [body, .. commentSources];
    }
}
