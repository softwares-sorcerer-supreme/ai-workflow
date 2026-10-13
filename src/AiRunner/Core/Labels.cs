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
    public const string Claimed = "ai:claimed";

    // set by a human
    public const string Breakdown = "ai-breakdown";
    public const string Implement = "ai-implement";
    public const string NeedsCloudAgent = "needs-cloud-agent";

    /// <summary>Every label the runner uses, with a color and description, for --setup-labels.</summary>
    public static IReadOnlyList<(string Name, string Color, string Description)> All(
        string triggerLabel,
        string triggerKeyword = "@ai") =>
    [
        (triggerLabel, "1d76db", "ai-runner looks at this issue"),
        (QuestionsOpen, "fbca04", $"Bot: analysis posted, author must answer (write {triggerKeyword})"),
        (Ready, "0e8a16", "Bot: no open questions left"),
        (Simple, "c2e0c6", "Bot: complexity guess = simple"),
        (Complex, "f9d0c4", "Bot: complexity guess = complex"),
        (Parent, "5319e7", "Bot: issue was split into sub-issues"),
        (SubIssue, "d4c5f9", "Bot: sub-issue of a complex issue"),
        (Implementing, "0052cc", "Bot: Copilot is working or opened a PR"),
        (OnGitHub, "bfdadc", "Bot: moved to the GitHub mirror"),
        (Failed, "b60205", "Bot: a job failed; remove this label to retry"),
        (Claimed, "ededed", "Bot: a runner works on this item (claim_enabled)"),
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

    /// <summary>Where a review fix that is still red is saved: never on the PR's own branch.</summary>
    public static string ReviewFix(int pull, int fix) => $"ai/pr-{pull}-fix-{fix}";

    public static bool IsAiBranch(string branch) =>
        branch.StartsWith("ai/", StringComparison.Ordinal) ||
        branch.StartsWith("feat/issue-", StringComparison.Ordinal);

    public static bool IsAiIssueBranch(string branch) => branch.StartsWith("ai/issue-", StringComparison.Ordinal);
}

