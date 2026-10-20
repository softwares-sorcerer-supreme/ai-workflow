using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>
/// One issue as the poller last saw it, kept only for the dashboard (the snapshot). <c>Parent</c>, <c>SubOrder</c>
/// and <c>Component</c> come from a sub-issue's body; they are empty for any other issue.
/// </summary>
public sealed record IssueSnapshot(
    string Repo,
    int Number,
    string Project,
    string Title,
    string State,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Assignees,
    DateTimeOffset UpdatedAt,
    DateTimeOffset SeenAt)
{
    public int? Parent { get; init; }

    /// <summary>The "Order: k of K" of a sub-issue: its PR comes from branch ai/issue-{parent}-{k}.</summary>
    public int? SubOrder { get; init; }

    public string? Component { get; init; }

    public string? HtmlUrl { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public bool IsOpen => string.Equals(State, SnapshotRules.Open, StringComparison.Ordinal);

    public bool HasLabel(string label) => Labels.Contains(label, StringComparer.OrdinalIgnoreCase);

    /// <summary>"owner/repo#n", the form <see cref="IssueRef"/> prints and a PR's linked issue is stored in.</summary>
    public string Key => new IssueRef(Repo, Number).ToString();
}

/// <summary>One PR as the poller last saw it, for the dashboard. <c>LinkedIssue</c> is "owner/repo#n".</summary>
public sealed record PullSnapshot(
    string Repo,
    int Number,
    string Project,
    string Title,
    string State,
    bool IsMerged,
    string HeadRef,
    string BaseRef,
    string HeadSha,
    DateTimeOffset UpdatedAt,
    DateTimeOffset SeenAt)
{
    public string? LinkedIssue { get; init; }

    public string? HtmlUrl { get; init; }

    /// <summary>The PR in the shape the jobs read, so the "its PR" helpers run unchanged on snapshot data.</summary>
    public GbPull AsPull() =>
        new(
            Number,
            Title,
            new GbRef(HeadRef, HeadSha),
            new GbRef(BaseRef, ""),
            UpdatedAt,
            State: State,
            Merged: IsMerged,
            HtmlUrl: HtmlUrl);
}

/// <summary>Where a listed PR comes from and when it was seen.</summary>
public sealed record PullSource(ProjectConfig Project, string Repo, DateTimeOffset SeenAt);

/// <summary>What decides whether a closed issue stays in the snapshot.</summary>
/// <param name="OpenNumbers">The issues of the project that are open on this poll.</param>
public sealed record RetentionWindow(DateTimeOffset Now, int RetentionDays, IReadOnlySet<int> OpenNumbers);

/// <summary>
/// How the poller turns what it lists into snapshot rows, and which closed issues and PRs the slow refresh keeps.
/// The snapshot exists only for the dashboard and can be thrown away: nothing here takes part in a planner decision.
/// </summary>
public static class SnapshotRules
{
    public const string Open = "open";
    public const string Closed = "closed";

    /// <summary>The text of a state code, as the dashboard shows it.</summary>
    public static string StateLabel(string state) => state == Open ? "Open" : "Closed";

    public static IssueSnapshot IssueRow(ProjectConfig project, GbIssue issue, DateTimeOffset seenAt)
    {
        var labels = issue.LabelNames().Order(StringComparer.OrdinalIgnoreCase).ToList();
        var assignees = issue.AssigneeLogins();
        var subIssueInfo = SubIssueInfoOf(issue);
        var state = issue.IsOpen() ? Open : Closed;

        return new IssueSnapshot(project.IssuesRepo, issue.Number, project.Name, issue.Title, state, labels, assignees,
            issue.UpdatedAt, seenAt)
        {
            Parent = subIssueInfo?.Parent,
            SubOrder = subIssueInfo?.Order,
            Component = subIssueInfo?.Component,
            HtmlUrl = issue.HtmlUrl,
            CreatedAt = issue.CreatedAt,
            ClosedAt = issue.ClosedAt,
        };
    }

    public static PullSnapshot PullRow(PullSource source, GbPull pull)
    {
        var (project, repo, seenAt) = source;
        var linkedIssue = IssueLinks.Find(pull.Body, repo, project);
        var state = pull.IsOpen() ? Open : Closed;

        return new PullSnapshot(repo, pull.Number, project.Name, pull.Title, state, pull.IsMerged(), pull.Head.Ref,
            pull.Base.Ref, pull.Head.Sha, pull.UpdatedAt, seenAt)
        {
            LinkedIssue = linkedIssue?.ToString(),
            HtmlUrl = pull.HtmlUrl,
        };
    }

    /// <summary>A closed issue was updated within the retention days.</summary>
    public static bool IsWithinRetention(IssueSnapshot issue, RetentionWindow window) =>
        issue.UpdatedAt >= window.Now.AddDays(-window.RetentionDays);

    /// <summary>
    /// The closed issues to keep: those within the retention days, and every closed sub-issue whose parent is kept
    /// (open, or closed and kept), so the parent's n/m and its final PRs stay right. Without the second part, a parent
    /// that closed after its last final PR was merged would lose its older sub-issues, and its stage would be worked
    /// out from the few that are left.
    /// </summary>
    public static IReadOnlyList<IssueSnapshot> KeepClosedIssues(
        IReadOnlyList<IssueSnapshot> closedIssues,
        RetentionWindow window)
    {
        var recentNumbers = closedIssues
            .Where(issue => IsWithinRetention(issue, window))
            .Select(issue => issue.Number)
            .ToHashSet();
        var keptParents = recentNumbers
            .Concat(window.OpenNumbers)
            .ToHashSet();

        return closedIssues
            .Where(issue => recentNumbers.Contains(issue.Number) || HasKeptParent(issue, keptParents))
            .ToList();
    }

    private static bool HasKeptParent(IssueSnapshot issue, IReadOnlySet<int> keptParents) =>
        issue.Parent is { } parent && keptParents.Contains(parent);

    /// <summary>A closed PR stays when the runner made it (its branch) and its linked issue is kept.</summary>
    /// <param name="issueKeys">"owner/repo#n" of every issue the snapshot keeps, case-insensitive.</param>
    public static bool KeepClosedPull(PullSnapshot pull, IReadOnlySet<string> issueKeys) =>
        Branches.IsRunnerBranch(pull.HeadRef) &&
        pull.LinkedIssue is { } linkedIssue &&
        issueKeys.Contains(linkedIssue);

    /// <summary>
    /// The component an issue is shown under: the one its sub-issue body names, else the project's only code
    /// component (a simple issue's own component is in its analysis comment, which the snapshot does not hold).
    /// </summary>
    public static string? ComponentOf(IssueSnapshot issue, ProjectConfig project) =>
        issue.Component ?? project.SoleCodeComponent?.Name;

    private static SubIssueInfo? SubIssueInfoOf(GbIssue issue)
    {
        try
        {
            return SubIssueBody.Parse(issue.Body);
        }
        catch (FormatException)
        {
            // A broken sub-issue body only means the dashboard does not nest it; it never stops the poll.
            return null;
        }
    }
}
