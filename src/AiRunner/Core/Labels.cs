using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>Label names from design doc section 5. The runner depends on the exact names.</summary>
public static class Labels
{
    // set by the bot
    public const string QuestionsOpen = "ai:questions-open";
    public const string Ready = "ai:ready";
    public const string Simple = "ai:simple";
    public const string Complex = "ai:complex";
    public const string Parent = "ai:parent";
    public const string SubIssue = "ai:sub-issue";
    public const string Implementing = "ai:implementing";
    public const string OnGitHub = "ai:on-github";
    public const string Failed = "ai:failed";

    // set by a human
    public const string Breakdown = "ai-breakdown";
    public const string Implement = "ai-implement";
    public const string NeedsCloudAgent = "needs-cloud-agent";

    /// <summary>Every label the runner uses, with a color and description, for --setup-labels.</summary>
    public static IReadOnlyList<(string Name, string Color, string Description)> All(string scopeLabel) =>
    [
        (scopeLabel, "1d76db", "ai-runner looks at this issue"),
        (QuestionsOpen, "fbca04", "Bot: analysis posted, author must answer (write @ai)"),
        (Ready, "0e8a16", "Bot: no open questions left"),
        (Simple, "c2e0c6", "Bot: complexity guess = simple"),
        (Complex, "f9d0c4", "Bot: complexity guess = complex"),
        (Parent, "5319e7", "Bot: issue was split into sub-issues"),
        (SubIssue, "d4c5f9", "Bot: sub-issue of a complex issue"),
        (Implementing, "0052cc", "Bot: Copilot is working or opened a PR"),
        (OnGitHub, "bfdadc", "Bot: moved to the GitHub mirror"),
        (Failed, "b60205", "Bot: a job failed; remove this label to retry"),
        (Breakdown, "006b75", "Human: breakdown approved, create sub-issues"),
        (Implement, "0e8a16", "Human: Copilot, build this"),
        (NeedsCloudAgent, "e99695", "Human: Copilot quota empty, use the GitHub mirror"),
    ];
}

/// <summary>Branch names used by the workflows.</summary>
public static class Branches
{
    /// <summary>The remote-tracking name of a branch in the runner's clone.</summary>
    public static string Remote(string branch) => $"origin/{branch}";

    /// <summary>Refspec for a PR's head commit (refs/pull/N/head); not every GitBucket version has it.</summary>
    public static string PullHead(int number) => $"+refs/pull/{number}/head:refs/remotes/pr/{number}";

    public static string Feature(int parent) => $"feat/issue-{parent}";
    public static string Issue(int number) => $"ai/issue-{number}";
    public static string SubIssue(int parent, int order) => $"ai/issue-{parent}-{order}";

    public static bool IsAiBranch(string branch) =>
        branch.StartsWith("ai/", StringComparison.Ordinal) || branch.StartsWith("feat/issue-", StringComparison.Ordinal);

    public static bool IsAiIssueBranch(string branch) => branch.StartsWith("ai/issue-", StringComparison.Ordinal);
}

/// <summary>
/// The runner posts with the developer's token, so it marks its own comments with a
/// hidden line and never reacts to them. The block markers stay in the posted text,
/// so the kind of a bot comment (analysis, SPEC, review) is found again without a database.
/// </summary>
public static class BotComments
{
    public const string Marker = "<!-- ai-runner -->";

    /// <summary>Visible first line, so nobody mistakes a bot comment for a colleague's opinion.</summary>
    public const string VisibleTag = "**[ai-runner]**";

    public static bool IsBot(GbComment c) => c.Body?.Contains(Marker, StringComparison.Ordinal) == true;

    /// <summary>A bot comment that holds a block of that kind (Markers.Analysis, Spec, Review).</summary>
    public static bool Is(GbComment c, string marker) => IsBot(c) && Markers.Contains(c.Body, marker);

    public static bool IsAnalysis(GbComment c) => Is(c, Markers.Analysis);

    /// <summary>The newest bot comment with a block of that kind, or null.</summary>
    public static GbComment? Latest(IEnumerable<GbComment> comments, string marker) =>
        comments.Where(c => Is(c, marker)).MaxBy(c => c.Id);

    public static bool MentionsAi(GbComment c) =>
        !IsBot(c) && c.Body is not null &&
        Regex.IsMatch(c.Body, @"(^|[^\w])@ai\b", RegexOptions.IgnoreCase);

    // A comment written while Copilot was running was not in context.md, but its id is lower
    // than the analysis comment posted after it. So the analysis comment remembers the newest
    // comment id it read; a later @ai comment then still counts as new, even without the database.
    private const string UptoPrefix = "<!-- ai-runner:context-upto=";

    public static string ContextUptoTag(long commentId) => $"{UptoPrefix}{commentId} -->";

    /// <summary>A bot block plus the newest comment id its job read, ready to post.</summary>
    public static string WithUpto(string inner, string marker, long upTo) => Markers.Wrap(inner, marker) + "\n" + ContextUptoTag(upTo);

    /// <summary>Newest comment id the latest bot block of that kind (or the local state) answered; later comments are new.</summary>
    public static long AnsweredUpTo(IEnumerable<GbComment> comments, string marker, long lastHandledCommentId)
    {
        var last = Latest(comments, marker);
        return Math.Max(lastHandledCommentId, last is null ? 0 : ContextUpto(last) ?? last.Id);
    }

    /// <summary>The newest human @ai comment after that id, or null.</summary>
    public static GbComment? NewAtAi(IEnumerable<GbComment> comments, long afterId) =>
        comments.Where(c => c.Id > afterId && MentionsAi(c)).MaxBy(c => c.Id);

    public static int Count(IEnumerable<GbComment> comments, string marker) => comments.Count(c => Is(c, marker));

    public static long? ContextUpto(GbComment c)
    {
        // The last tag: the runner appends it after the text it posts, which could contain a fake one.
        var m = Regex.Matches(c.Body ?? "", Regex.Escape(UptoPrefix) + @"(\d+) -->").LastOrDefault();
        return m is null ? null : long.Parse(m.Groups[1].Value);
    }
}
