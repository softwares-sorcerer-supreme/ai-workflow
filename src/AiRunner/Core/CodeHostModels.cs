using System.Text.Json;

namespace AiRunner.Core;

// Issues, PRs and comments as the workflow needs them, whatever the code host. Each provider maps its host's
// answers onto these (GitBucket: its GitHub-compatible /api/v3 JSON). Only the fields the runner reads; fields a
// host may leave out are nullable.

public sealed record HostUser(string Login);

public sealed record HostLabel(string Name);

public sealed record HostRepo(string? DefaultBranch);

public sealed record HostIssue(
    int Number,
    string Title,
    string? Body,
    HostUser User,
    List<HostLabel>? Labels,
    DateTimeOffset UpdatedAt,
    JsonElement? PullRequest,
    string? State = null,
    List<HostUser>? Assignees = null,
    HostUser? Assignee = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? ClosedAt = null,
    string? HtmlUrl = null);

public sealed record HostComment(long Id, string? Body, HostUser User, DateTimeOffset CreatedAt);

public sealed record HostRef(string Ref, string Sha);

public sealed record HostPull(
    int Number,
    string Title,
    HostRef Head,
    HostRef Base,
    DateTimeOffset UpdatedAt,
    string? Body = null,
    HostUser? User = null,
    string? State = null,
    bool? Merged = null,
    DateTimeOffset? MergedAt = null,
    bool? Draft = null,
    List<HostLabel>? Labels = null,
    string? HtmlUrl = null);

public static class CodeHostModelExtensions
{
    public static HashSet<string> LabelNames(this HostIssue issue) => Names(issue.Labels);
    public static HashSet<string> LabelNames(this HostPull pull) => Names(pull.Labels);

    public static bool HasLabel(this HostIssue issue, string label) => issue.LabelNames().Contains(label);

    /// <summary>
    /// updated_at plus the labels: a change in either means the issue must be planned again.
    /// Labels are part of it because GitBucket may not bump updated_at when a label changes.
    /// </summary>
    public static string Fingerprint(this HostIssue issue)
    {
        var updatedAt = issue.UpdatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var labelNames = issue.LabelNames().Select(label => label.ToLowerInvariant()).Order(StringComparer.Ordinal);
        var joinedLabels = string.Join(",", labelNames);

        return updatedAt + "|" + joinedLabels;
    }

    public static bool IsOpen(this HostIssue issue) => IsOpenState(issue.State);

    public static bool IsPullRequest(this HostIssue issue) =>
        issue.PullRequest is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };

    /// <summary>Checks "assignees" and the older single "assignee" field.</summary>
    public static bool IsAssignedTo(this HostIssue issue, string login) =>
        issue.AssigneeLogins().Contains(login, StringComparer.OrdinalIgnoreCase);

    /// <summary>The logins in "assignees" and the older single "assignee" field, each once.</summary>
    public static IReadOnlyList<string> AssigneeLogins(this HostIssue issue) =>
        (issue.Assignees ?? [])
            .Append(issue.Assignee)
            .OfType<HostUser>()
            .Select(user => user.Login)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Open and not merged. A missing state counts as open (lists of open PRs may leave it out).</summary>
    public static bool IsOpen(this HostPull pull) => !pull.IsMerged() && IsOpenState(pull.State);

    /// <summary>
    /// A host may set only one of the two (GitBucket: "merged" on newer versions, "merged_at" on all); trust either.
    /// </summary>
    public static bool IsMerged(this HostPull pull) => pull.Merged == true || pull.MergedAt is not null;

    /// <summary>The PR from head into base, or null.</summary>
    public static HostPull? Find(this IEnumerable<HostPull> pulls, string head, string baseRef) =>
        pulls.FirstOrDefault(pull => pull.Head.Ref == head && pull.Base.Ref == baseRef);

    private static bool IsOpenState(string? state) =>
        state is null || string.Equals(state, "open", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> Names(List<HostLabel>? labels) =>
        (labels ?? []).Select(label => label.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
