namespace AiRunner.Core;

/// <summary>
/// Checks REPLY.md of a review fix against templates/review-reply.md. Like PrBodyParser it does not
/// throw: the problems go back to the agent in a fix round, and whatever is left is listed in the reply.
/// </summary>
public static class ReviewReplyParser
{
    public static readonly string[] Headings = ["Replies", "How it was tested", "Open points"];

    /// <summary>Empty when REPLY.md is complete.</summary>
    public static IReadOnlyList<string> Problems(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return ["REPLY.md is missing or empty."];

        reply = MarkdownSections.HeadingsToLevel3(reply);
        var problems = MarkdownSections.HeadingProblems(reply, Headings, "Open points");
        var replies = MarkdownSections.Get(reply, "Replies");
        if (replies is not null && !MarkdownSections.IsNone(replies) && !MarkdownSections.HasListItem(replies))
            problems.Add("`### Replies` has no `- ` lines. Write one line per request: `- <which>: fixed - <what>` or `- <which>: not changed - <why>`.");
        return problems;
    }
}
