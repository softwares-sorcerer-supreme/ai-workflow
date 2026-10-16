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
    private const string RemotePrefix = "origin/";

    private const string FeaturePrefix = "feat/issue-";

    /// <summary>The remote-tracking name of a branch in the runner's clone.</summary>
    public static string Remote(string branch) => $"{RemotePrefix}{branch}";

    /// <summary>The branch of a remote-tracking ref; a ref without the "origin/" prefix stays as it is.</summary>
    public static string LocalName(string remoteRef) =>
        remoteRef.StartsWith(RemotePrefix, StringComparison.Ordinal) ? remoteRef[RemotePrefix.Length..] : remoteRef;

    /// <summary>Refspec for a PR's head commit (refs/pull/N/head); not every GitBucket version has it.</summary>
    public static string PullHead(int number) => $"+refs/pull/{number}/head:refs/remotes/pr/{number}";

    public static string Feature(int parent) => $"{FeaturePrefix}{parent}";
    public static string Issue(int number) => $"ai/issue-{number}";
    public static string SubIssue(int parent, int order) => $"ai/issue-{parent}-{order}";

    /// <summary>Where a review fix that is still red is saved: never on the PR's own branch.</summary>
    public static string ReviewFix(int pull, int fix) => $"ai/pr-{pull}-fix-{fix}";

    public static bool IsAiBranch(string branch) =>
        branch.StartsWith("ai/", StringComparison.Ordinal) || IsFeature(branch);

    public static bool IsAiIssueBranch(string branch) => branch.StartsWith("ai/issue-", StringComparison.Ordinal);

    /// <summary>True for the feature branch of a parent issue (feat/issue-N).</summary>
    public static bool IsFeature(string branch) => branch.StartsWith(FeaturePrefix, StringComparison.Ordinal);

    /// <summary>
    /// The feature branch a PR belongs to: its base (a sub-issue PR into feat/issue-N), else its head (the final PR
    /// feat/issue-N into the default branch), else null. The other repos of the project are read at that branch,
    /// because that is where the work of the sibling sub-issues is.
    /// </summary>
    public static string? FeatureBranchOf(GbPull pull)
    {
        if (IsFeature(pull.Base.Ref))
        {
            return pull.Base.Ref;
        }

        return IsFeature(pull.Head.Ref) ? pull.Head.Ref : null;
    }
}

