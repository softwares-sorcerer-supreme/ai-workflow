using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <param name="Component">The one code component this sub-task changes; its repo gets the PR.</param>
/// <param name="NeedsDeveloper">The agent could not make this part simple; a developer builds it.</param>
public sealed record SubTask(
    int Order,
    string Component,
    string Title,
    string Scope,
    string Touches,
    IReadOnlyList<int> DependsOn,
    bool NeedsDeveloper)
{
    /// <summary>
    /// The SPEC technical plan steps this sub-task builds. Empty for a breakdown written before the Steps column;
    /// the plan of the sub-issue then marks no step.
    /// </summary>
    public IReadOnlyList<int> Steps { get; init; } = [];
}

public sealed record Breakdown(IReadOnlyList<SubTask> Tasks, string SharedContract);

/// <summary>
/// Reads "### Proposed breakdown" from an analysis block (Workflow 1b): a table
/// | # | Component | Title | Scope | Touches | Steps | Depends on | and a "Shared contract:" line.
/// The table of older analyses has no Steps column (6 cells) and still reads.
/// Each row names exactly one code component of the project.
/// The layout is fixed by templates/analysis.md; anything else fails clearly.
/// </summary>
public static class BreakdownParser
{
    private const string Heading = "Proposed breakdown";
    private const string DeveloperTag = "[developer]";

    public static Breakdown Parse(string analysisBlock, IReadOnlyCollection<string> codeComponents)
    {
        var section = MarkdownSections.Get(analysisBlock, Heading)
            ?? throw new FormatException($"'### {Heading}' section not found in the latest analysis");

        if (MarkdownSections.IsNone(section))
        {
            throw new FormatException($"'### {Heading}' is None; ask for a breakdown in a comment first");
        }

        var tasks = new List<SubTask>();

        foreach (var cells in MarkdownSections.TableRows(section))
        {
            if (cells.Length is not (6 or 7))
            {
                throw new FormatException(
                    "breakdown row must have 7 cells (# | Component | Title | Scope | Touches | Steps | Depends on; " +
                    "an older table without Steps has 6), " +
                    $"got {cells.Length}: '{string.Join(" | ", cells)}'");
            }

            var hasSteps = cells.Length == 7;
            var dependsOnCell = cells[^1];

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

            var component = ComponentNames.ParseOne(cells[1], codeComponents, $"breakdown row {order}");
            var title = cells[2];
            var needsDeveloper = title.StartsWith(DeveloperTag, StringComparison.OrdinalIgnoreCase);

            if (needsDeveloper)
            {
                title = title[DeveloperTag.Length..].Trim();
            }

            if (title.Length == 0 || cells[3].Length == 0)
            {
                throw new FormatException($"breakdown row {order} has an empty title or scope");
            }

            var dependencies = Regex.Matches(dependsOnCell, @"\d+")
                .Select(match => int.Parse(match.Value))
                .Distinct()
                .ToList();

            if (dependencies.Any(dependency => dependency >= order))
            {
                throw new FormatException($"breakdown row {order} depends on a later or same row ({dependsOnCell})");
            }

            var steps = hasSteps ? SpecParser.StepNumbers(cells[5]) : [];
            tasks.Add(new SubTask(order, component, title, cells[3], cells[4], dependencies, needsDeveloper)
            {
                Steps = steps,
            });
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

    /// <summary>
    /// Checks the Steps column against the SPEC of the same analysis: every step a row names exists in the technical
    /// plan and belongs to the row's component. A mistake here would mark the wrong steps in a sub-issue's plan.
    /// <see cref="SpecParser.Parse"/> numbers the steps by their Step cell, the same rule the marks use.
    /// </summary>
    public static void CheckSteps(Breakdown breakdown, IReadOnlyList<SpecStep> specSteps)
    {
        var componentOfStep = specSteps.ToDictionary(step => step.Step, step => step.Component);

        foreach (var task in breakdown.Tasks)
        {
            foreach (var step in task.Steps)
            {
                if (!componentOfStep.TryGetValue(step, out var component))
                {
                    throw new FormatException(
                        $"breakdown row {task.Order} names step {step}, which is not in the SPEC technical plan");
                }

                if (!string.Equals(component, task.Component, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException(
                        $"breakdown row {task.Order} ({task.Component}) names step {step}, " +
                        $"which belongs to component {component}");
                }
            }
        }
    }
}
