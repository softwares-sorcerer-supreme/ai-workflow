using System.Text;
using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <param name="Component">The component this sub-issue is built in. Null for a sub-issue written before components existed.</param>
public sealed record SubIssueInfo(int Parent, int Order, int Total, string BaseBranch, string? Component);

/// <summary>Which sub-issue of which parent: "#12.2" is Parent 12, Order 2.</summary>
public sealed record SubIssueSlot(int Parent, int Order);

/// <summary>What <see cref="SubIssueBody.Build"/> needs to write the body of a new sub-issue.</summary>
/// <param name="Total">How many sub-issues the breakdown has.</param>
public sealed record SubIssueDraft(int Parent, SubTask Task, int Total, string SharedContract);

/// <summary>
/// The body of a sub-issue created by Workflow 1b. The first lines are fixed so the
/// implement job reads parent, order and base branch from the issue itself; no database.
/// </summary>
public static class SubIssueBody
{
    public static string Title(int parent, SubTask task) => $"#{parent}.{task.Order} {task.Title}";

    public static string Build(SubIssueDraft draft)
    {
        var (parent, task, total, sharedContract) = draft;
        var dependencies = "-";

        // Only a task with dependencies lists them.
        if (task.DependsOn.Count > 0)
        {
            var dependencyNames = task.DependsOn.Select(dependsOn => $"#{parent}.{dependsOn}");
            dependencies = string.Join(", ", dependencyNames);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Parent: #{parent}");
        builder.AppendLine($"Component: {task.Component}");
        builder.AppendLine($"Order: {task.Order} of {total}");
        builder.AppendLine($"Base branch: {Branches.Feature(parent)}");
        builder.AppendLine($"Depends on: {dependencies}");
        builder.AppendLine();

        if (task.NeedsDeveloper)
        {
            builder.AppendLine("**A developer builds this sub-task.** The analysis could not make it simple enough for Copilot.");
            builder.AppendLine("When it is done, close this issue, and the next sub-issue starts.");
            builder.AppendLine(
                $"A PR counts only from branch `{Branches.SubIssue(parent, task.Order)}` into `{Branches.Feature(parent)}`.");
            builder.AppendLine();
        }

        builder.AppendLine("### Scope");
        builder.AppendLine(task.Scope);
        builder.AppendLine();
        builder.AppendLine("### Touches");
        var touches = string.IsNullOrWhiteSpace(task.Touches) ? "None" : task.Touches;
        builder.AppendLine(touches);
        builder.AppendLine();
        builder.AppendLine("### Shared contract (exact names, used by every sub-issue)");
        builder.AppendLine(sharedContract);

        return builder.ToString().TrimEnd();
    }

    /// <summary>Null when the body is not a sub-issue body (no "Parent: #n" line).</summary>
    public static SubIssueInfo? Parse(string? body)
    {
        if (body is null)
        {
            return null;
        }

        var parentMatch = Regex.Match(body, @"^Parent:\s*#(\d+)\s*$", RegexOptions.Multiline);

        if (!parentMatch.Success)
        {
            return null;
        }

        var orderMatch = Regex.Match(body, @"^Order:\s*(\d+)\s*of\s*(\d+)\s*$", RegexOptions.Multiline);

        if (!orderMatch.Success)
        {
            throw new FormatException("sub-issue body has 'Parent:' but no 'Order: k of K' line");
        }

        var parentNumber = int.Parse(parentMatch.Groups[1].Value);
        var baseBranchMatch = Regex.Match(body, @"^Base branch:\s*(\S+)\s*$", RegexOptions.Multiline);
        var componentMatch = Regex.Match(body, @"^Component:\s*(\S+)\s*$", RegexOptions.Multiline);

        var order = int.Parse(orderMatch.Groups[1].Value);
        var total = int.Parse(orderMatch.Groups[2].Value);
        var baseBranch = baseBranchMatch.Success ? baseBranchMatch.Groups[1].Value : Branches.Feature(parentNumber);
        var component = componentMatch.Success ? componentMatch.Groups[1].Value : null;
        return new SubIssueInfo(parentNumber, order, total, baseBranch, component);
    }

    /// <summary>True when the issue is the sub-issue number "order" of "parent" (read from its body, not its title).</summary>
    public static bool IsFor(GbIssue issue, int parent, int order)
    {
        try
        {
            return Parse(issue.Body) is { } info && info.Parent == parent && info.Order == order;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// A sub-issue is done when its PR (branch ai/issue-{parent}-{order}) is merged into the
    /// feature branch, or when a human closed it (e.g. a developer built it another way).
    /// </summary>
    public static bool IsDone(GbIssue sub, SubIssueSlot slot, IEnumerable<GbPull> pulls) =>
        !sub.IsOpen() || SubIssueSequencer.MergedPull(slot, pulls) is not null;

    // ---- the list of sub-issues, kept as a hidden line in the parent comment ----

    private const string ListPrefix = "<!-- SUBISSUES:";

    /// <summary>Hidden tag of the parent comment that lists the final PRs (one per repo).</summary>
    public const string FinalPrsMarker = "<!-- FINAL_PRS -->";

    /// <summary>True when the parent already has the comment that lists its final PRs.</summary>
    public static bool HasFinalList(IEnumerable<GbComment> comments) =>
        comments.Any(comment => BotComments.IsBot(comment) &&
                                comment.Body!.Contains(FinalPrsMarker, StringComparison.Ordinal));

    public static string ListMarker(IEnumerable<int> numbers) => $"{ListPrefix} {string.Join(",", numbers)} -->";

    /// <summary>Sub-issue numbers in order, from the newest bot comment that has the hidden list.</summary>
    public static IReadOnlyList<int>? FindList(IEnumerable<GbComment> comments)
    {
        var botComments = comments.Where(BotComments.IsBot).OrderByDescending(comment => comment.Id);

        foreach (var comment in botComments)
        {
            var listPattern = Regex.Escape(ListPrefix) + @"\s*([\d,\s]+)-->";
            var match = Regex.Match(comment.Body!, listPattern);

            if (match.Success)
            {
                return match.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(int.Parse)
                    .ToList();
            }
        }

        return null;
    }
}
