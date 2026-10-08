using System.Text;
using System.Text.RegularExpressions;

namespace AiRunner.Core;

public sealed record SubIssueInfo(int Parent, int Order, int Total, string BaseBranch);

/// <summary>
/// The body of a sub-issue created by Workflow 1b. The first lines are fixed so the
/// implement job reads parent, order and base branch from the issue itself; no database.
/// </summary>
public static class SubIssueBody
{
    public static string Title(int parent, SubTask task) => $"#{parent}.{task.Order} {task.Title}";

    public static string Build(int parent, SubTask task, int total, string sharedContract)
    {
        var deps = task.DependsOn.Count == 0 ? "-" : string.Join(", ", task.DependsOn.Select(d => $"#{parent}.{d}"));
        var sb = new StringBuilder();
        sb.AppendLine($"Parent: #{parent}");
        sb.AppendLine($"Order: {task.Order} of {total}");
        sb.AppendLine($"Base branch: {Branches.Feature(parent)}");
        sb.AppendLine($"Depends on: {deps}");
        sb.AppendLine();
        if (task.NeedsDeveloper)
        {
            sb.AppendLine("**A developer builds this sub-task.** The analysis could not make it simple enough for Copilot.");
            sb.AppendLine($"When it is done, merge it into `{Branches.Feature(parent)}` or close this issue, and the next sub-issue starts.");
            sb.AppendLine();
        }
        sb.AppendLine("### Scope");
        sb.AppendLine(task.Scope);
        sb.AppendLine();
        sb.AppendLine("### Touches");
        sb.AppendLine(string.IsNullOrWhiteSpace(task.Touches) ? "None" : task.Touches);
        sb.AppendLine();
        sb.AppendLine("### Shared contract (exact names, used by every sub-issue)");
        sb.AppendLine(sharedContract);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Null when the body is not a sub-issue body (no "Parent: #n" line).</summary>
    public static SubIssueInfo? Parse(string? body)
    {
        if (body is null) return null;
        var parent = Regex.Match(body, @"^Parent:\s*#(\d+)\s*$", RegexOptions.Multiline);
        if (!parent.Success) return null;

        var order = Regex.Match(body, @"^Order:\s*(\d+)\s*of\s*(\d+)\s*$", RegexOptions.Multiline);
        if (!order.Success)
            throw new FormatException("sub-issue body has 'Parent:' but no 'Order: k of K' line");

        var p = int.Parse(parent.Groups[1].Value);
        var baseBranch = Regex.Match(body, @"^Base branch:\s*(\S+)\s*$", RegexOptions.Multiline);
        return new SubIssueInfo(p, int.Parse(order.Groups[1].Value), int.Parse(order.Groups[2].Value),
            baseBranch.Success ? baseBranch.Groups[1].Value : Branches.Feature(p));
    }

    /// <summary>True when the issue is the sub-issue number "order" of "parent" (read from its body, not its title).</summary>
    public static bool IsFor(GbIssue issue, int parent, int order)
    {
        try { return Parse(issue.Body) is { } info && info.Parent == parent && info.Order == order; }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// A sub-issue is done when its PR (branch ai/issue-{parent}-{order}) is merged into the
    /// feature branch, or when a human closed it (e.g. a developer built it another way).
    /// </summary>
    public static bool IsDone(GbIssue sub, int parent, int order, IEnumerable<GbPull> pulls) =>
        !sub.IsOpen() ||
        pulls.Any(p => p.Head.Ref == Branches.SubIssue(parent, order) && p.Base.Ref == Branches.Feature(parent) && p.IsMerged());

    // ---- the list of sub-issues, kept as a hidden line in the parent comment ----

    private const string ListPrefix = "<!-- SUBISSUES:";

    public static string ListMarker(IEnumerable<int> numbers) => $"{ListPrefix} {string.Join(",", numbers)} -->";

    /// <summary>Sub-issue numbers in order, from the newest bot comment that has the hidden list.</summary>
    public static IReadOnlyList<int>? FindList(IEnumerable<GbComment> comments)
    {
        foreach (var c in comments.Where(BotComments.IsBot).OrderByDescending(c => c.Id))
        {
            var m = Regex.Match(c.Body!, Regex.Escape(ListPrefix) + @"\s*([\d,\s]+)-->");
            if (m.Success)
                return m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(int.Parse).ToList();
        }
        return null;
    }
}
