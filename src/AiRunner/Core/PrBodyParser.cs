using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>
/// Checks PR_BODY.md against templates/pr-body.md. Unlike the other parsers
/// it does not throw: the problems go back to the agent in a fix round, and whatever is left is
/// listed in the PR, so a weak PR_BODY never loses the code.
/// </summary>
public static class PrBodyParser
{
    public static readonly string[] Headings = ["What changed", "How it was tested", "Acceptance criteria", "Open points"];

    // Top-level checkbox lines only (at most one leading space): nested sub-items are not extra criteria.
    // Any list marker counts, "- [x]", "* [x]", "+ [x]", "1. [x]", because a slip there is not worth a fix round.
    private static readonly Regex Criterion = new(@"^ ?(?:[-*+]|\d+[.)])[ \t]*\[(?<mark>[ xX])\][ \t]*(?<text>.*)$", RegexOptions.Multiline);
    private static readonly Regex Reason = new(@"\s-+\s+\S");
    private static readonly Regex Numbered = new(@"^\s*\d+[.)]\s+\S", RegexOptions.Multiline);

    /// <summary>Empty when PR_BODY.md is complete. plan.md is used to count the acceptance criteria.</summary>
    public static IReadOnlyList<string> Problems(string? prBody, string plan)
    {
        if (string.IsNullOrWhiteSpace(prBody))
            return ["PR_BODY.md is missing or empty."];

        prBody = MarkdownSections.HeadingsToLevel3(prBody);
        var problems = MarkdownSections.HeadingProblems(prBody, Headings, "Open points");

        var criteriaSection = MarkdownSections.Get(prBody, "Acceptance criteria");
        if (criteriaSection is null || MarkdownSections.IsNone(criteriaSection)) return problems;

        var criteria = Criterion.Matches(criteriaSection);
        if (criteria.Count == 0)
        {
            problems.Add("`### Acceptance criteria` has no `- [x]` or `- [ ]` lines.");
            return problems;
        }
        foreach (Match c in criteria)
            if (c.Groups["mark"].Value == " " && !Reason.IsMatch(c.Groups["text"].Value))
                problems.Add($"Unchecked criterion without a reason: `{c.Value.Trim()}`. Write ` - <why not covered>` after it.");

        var planned = MarkdownSections.Get(plan, "Acceptance criteria") is { } planSection ? Numbered.Matches(planSection).Count : 0;
        if (planned > 0 && criteria.Count != planned)
            problems.Add($"plan.md has {planned} acceptance criteria but PR_BODY.md lists {criteria.Count}. List each one once.");
        return problems;
    }
}
