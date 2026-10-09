using System.Text.Json;

namespace AiRunner.Core;

// GitHub-compatible JSON from GitBucket /api/v3. Only the fields the runner reads;
// fields GitBucket may leave out are nullable.

public sealed record GbUser(string Login);

public sealed record GbLabel(string Name);

public sealed record GbRepo(string? DefaultBranch);

public sealed record GbIssue(
    int Number,
    string Title,
    string? Body,
    GbUser User,
    List<GbLabel>? Labels,
    DateTimeOffset UpdatedAt,
    JsonElement? PullRequest,
    string? State = null,
    List<GbUser>? Assignees = null,
    GbUser? Assignee = null);

public sealed record GbComment(long Id, string? Body, GbUser User, DateTimeOffset CreatedAt);

public sealed record GbRef(string Ref, string Sha);

public sealed record GbPull(
    int Number,
    string Title,
    GbRef Head,
    GbRef Base,
    DateTimeOffset UpdatedAt,
    string? Body = null,
    GbUser? User = null,
    string? State = null,
    bool? Merged = null,
    DateTimeOffset? MergedAt = null,
    bool? Draft = null,
    List<GbLabel>? Labels = null);

public static class GbExtensions
{
    public static HashSet<string> LabelNames(this GbIssue issue) => Names(issue.Labels);
    public static HashSet<string> LabelNames(this GbPull pull) => Names(pull.Labels);

    public static bool HasLabel(this GbIssue issue, string label) => issue.LabelNames().Contains(label);

    /// <summary>
    /// updated_at plus the labels: a change in either means the issue must be planned again.
    /// Labels are part of it because GitBucket may not bump updated_at when a label changes.
    /// </summary>
    public static string Fingerprint(this GbIssue issue)
    {
        var updatedAt = issue.UpdatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var labelNames = issue.LabelNames().Select(label => label.ToLowerInvariant()).Order(StringComparer.Ordinal);
        var joinedLabels = string.Join(",", labelNames);

        return updatedAt + "|" + joinedLabels;
    }

    public static bool IsOpen(this GbIssue issue) => IsOpenState(issue.State);

    public static bool IsPullRequest(this GbIssue issue) =>
        issue.PullRequest is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };

    /// <summary>Checks "assignees" and the older single "assignee" field.</summary>
    public static bool IsAssignedTo(this GbIssue issue, string login) => AnyIs(issue.Assignees, issue.Assignee, login);

    /// <summary>Open and not merged. A missing state counts as open (lists of open PRs may leave it out).</summary>
    public static bool IsOpen(this GbPull pull) => !pull.IsMerged() && IsOpenState(pull.State);

    /// <summary>GitBucket sets "merged" on newer versions and "merged_at" on all; trust either.</summary>
    public static bool IsMerged(this GbPull pull) => pull.Merged == true || pull.MergedAt is not null;

    /// <summary>The PR from head into base, or null.</summary>
    public static GbPull? Find(this IEnumerable<GbPull> pulls, string head, string baseRef) =>
        pulls.FirstOrDefault(pull => pull.Head.Ref == head && pull.Base.Ref == baseRef);

    private static bool IsOpenState(string? state) =>
        state is null || string.Equals(state, "open", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> Names(List<GbLabel>? labels) =>
        (labels ?? []).Select(label => label.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool AnyIs(List<GbUser>? users, GbUser? single, string login) =>
        (users ?? [])
            .Append(single)
            .Any(user => string.Equals(user?.Login, login, StringComparison.OrdinalIgnoreCase));
}
