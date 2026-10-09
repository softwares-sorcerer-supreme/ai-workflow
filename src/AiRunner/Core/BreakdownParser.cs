using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <param name="NeedsDeveloper">The agent could not make this part simple; a developer builds it.</param>
public sealed record SubTask(
    int Order,
    string Title,
    string Scope,
    string Touches,
    IReadOnlyList<int> DependsOn,
    bool NeedsDeveloper);

public sealed record Breakdown(IReadOnlyList<SubTask> Tasks, string SharedContract);

/// <summary>
/// Reads "### Proposed breakdown" from an analysis block (Workflow 1b): a table
/// | # | Title | Scope | Touches | Depends on | and a "Shared contract:" line.
/// The layout is fixed by templates/analysis.md; anything else fails clearly.
/// </summary>
public static class BreakdownParser
{
    private const string Heading = "Proposed breakdown";
    private const string DeveloperTag = "[developer]";

    public static Breakdown Parse(string analysisBlock)
    {
        var section = MarkdownSections.Get(analysisBlock, Heading)
            ?? throw new FormatException($"'### {Heading}' section not found in the latest analysis");

        if (MarkdownSections.IsNone(section))
        {
            throw new FormatException($"'### {Heading}' is None; ask for a breakdown with @ai first");
        }

        var tasks = new List<SubTask>();

        foreach (var cells in MarkdownSections.TableRows(section))
        {
            if (cells.Length != 5)
            {
                throw new FormatException(
                    $"breakdown row must have 5 cells (# | Title | Scope | Touches | Depends on), got {cells.Length}: '{string.Join(" | ", cells)}'");
            }

            var orderText = cells[0].Trim('#', ' ');
            if (!int.TryParse(orderText, out var order))
            {
                throw new FormatException($"breakdown row number '{cells[0]}' is not a number");
            }

            if (order != tasks.Count + 1)
            {
                throw new FormatException(
                    $"breakdown rows must be numbered 1, 2, 3 ... in order; found {order} at position {tasks.Count + 1}");
            }

            var title = cells[1];
            var needsDeveloper = title.StartsWith(DeveloperTag, StringComparison.OrdinalIgnoreCase);

            if (needsDeveloper)
            {
                title = title[DeveloperTag.Length..].Trim();
            }

            if (title.Length == 0 || cells[2].Length == 0)
            {
                throw new FormatException($"breakdown row {order} has an empty title or scope");
            }

            var dependencies = Regex.Matches(cells[4], @"\d+")
                .Select(match => int.Parse(match.Value))
                .Distinct()
                .ToList();

            if (dependencies.Any(dependency => dependency >= order))
            {
                throw new FormatException($"breakdown row {order} depends on a later or same row ({cells[4]})");
            }

            tasks.Add(new SubTask(order, title, cells[2], cells[3], dependencies, needsDeveloper));
        }

        if (tasks.Count == 0)
        {
            throw new FormatException($"'### {Heading}' has no table rows");
        }

        var contract = Regex.Match(
            section,
            @"^[\s*_-]*Shared contract[\s*_]*:[\s*_]*(?<v>.+)$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!contract.Success || string.IsNullOrWhiteSpace(contract.Groups["v"].Value))
        {
            throw new FormatException("'Shared contract:' line not found under the breakdown");
        }

        var sharedContract = contract.Groups["v"].Value.Trim();
        return new Breakdown(tasks, sharedContract);
    }
}
