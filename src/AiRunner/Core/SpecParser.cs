using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>
/// One step of the technical plan in a SPEC: which component, which file (relative to the component folder), the
/// existing file whose structure it must follow (<c>-</c> when no file of that kind exists), what changes.
/// </summary>
public sealed record SpecStep(int Step, string Component, string File, string PatternFrom, string Change);

/// <summary>
/// Checks "### Technical plan" in a SPEC block (templates/spec.md): a table
/// | Step | Component | File | Pattern from | Change | where every Component is one code component of the project.
/// Other sections are free text and not checked here. A bad SPEC fails the job like a bad Status would.
/// </summary>
public static class SpecParser
{
    private const string Heading = "Technical plan";

    /// <summary>What marks a sub-issue's own steps in the Step cell of its plan.</summary>
    public const string OwnStepMark = "(yours)";

    /// <summary>
    /// The highest step number a Steps list may name. A feature plan has tens of steps, never thousands, and the cap
    /// keeps a mistyped range ("1-2000000000") from growing into billions of numbers.
    /// </summary>
    private const int MaxStepNumber = 999;

    private static readonly Regex StepItem = new(@"(?<from>\d+)(\s*-\s*(?<to>\d+))?");

    public const string ApiHeading = "API changes";
    public const string UiHeading = "UI changes";
    private const string ApiColumns = "Method | Path | Query / paging | Request | Response | Errors | Auth";
    private const string UiColumns = "Route | Page / component | Copy from | Shared components | States";

    /// <summary>
    /// Checks the contract sections of a SPEC: "### API changes" (7 cells per row) and "### UI changes" (5 cells per
    /// row). Each is None or a table; extra cells join the last column, like in the technical plan.
    /// </summary>
    public static void Check(string specBlock)
    {
        CheckTable(specBlock, ApiHeading, ApiColumns);
        CheckTable(specBlock, UiHeading, UiColumns);
    }

    /// <summary>The cells of a row are the names in <paramref name="columns"/> ("A | B | C").</summary>
    private static void CheckTable(string specBlock, string heading, string columns)
    {
        var cellCount = columns.Split('|').Length;
        var section = MarkdownSections.Get(specBlock, heading)
            ?? throw new FormatException(
                $"'### {heading}' section not found in the SPEC (write None when there is nothing)");

        if (MarkdownSections.IsNone(section))
        {
            return;
        }

        var rows = MarkdownSections.TableRows(section);
        if (rows.Count == 0)
        {
            throw new FormatException(
                $"'### {heading}' must be None or a table ({columns}) with at least one row");
        }

        foreach (var cells in rows.Where(cells => cells.Length < cellCount))
        {
            throw new FormatException(
                $"'### {heading}' row must have {cellCount} cells ({columns}), " +
                $"got {cells.Length}: '{string.Join(" | ", cells)}'");
        }
    }

    /// <summary>
    /// The SPEC with only the technical plan steps of <paramref name="component"/>, for the plan of a sub-issue built
    /// in it: the other steps are built by other sub-issues, and every agent turn pays for each row it carries (a
    /// feature's plan is often more than half of its SPEC). The contract sections stay whole. When no step names the
    /// component (old data, a typo) or every step does, the SPEC comes back unchanged.
    /// </summary>
    public static string StepsOf(string specBlock, string component)
    {
        var section = MarkdownSections.Get(specBlock, Heading);
        if (section is null)
        {
            return specBlock;
        }

        var rows = MarkdownSections.TableRows(section);
        var ownCount = rows.Count(cells => IsStepOf(cells, component));
        if (ownCount == 0 || ownCount == rows.Count)
        {
            return specBlock;
        }

        return MarkdownSections.MapRows(
            specBlock,
            Heading,
            (line, cells) => IsStepOf(cells, component) ? line : null);
    }

    /// <summary>
    /// The SPEC with the technical plan steps of a sub-issue marked: their Step cell gets <see cref="OwnStepMark"/>.
    /// The other steps stay, so the agent still sees the whole plan of its component and only learns which rows it
    /// builds. A step is matched by <see cref="StepNumberOf"/>, the number <see cref="Parse"/> gives it too.
    /// </summary>
    public static string MarkSteps(string specBlock, IReadOnlyCollection<int> steps)
    {
        if (steps.Count == 0)
        {
            return specBlock;
        }

        return MarkdownSections.MapRows(
            specBlock,
            Heading,
            (line, cells) => IsOneOfSteps(cells, steps) ? MarkedRow(cells) : line);
    }

    /// <summary>
    /// The number a Step cell holds ("3", "3.", "**3**"), or null for a label without one. The breakdown's Steps
    /// column names steps by this number, so <see cref="Parse"/> and the marks in a sub-issue's plan both read it here.
    /// </summary>
    public static int? StepNumberOf(string cell)
    {
        var text = cell.Trim().Trim('*', '`', '.', ' ');
        if (int.TryParse(text, out var number))
        {
            return number;
        }

        return null;
    }

    /// <summary>
    /// The step numbers of a Steps list (the breakdown's column, a sub-issue's `Steps:` line): "3, 5", a range "7-9",
    /// or "-" for none. Sorted, without duplicates. A number above <see cref="MaxStepNumber"/> or a range that goes
    /// backwards is a <see cref="FormatException"/>, never an overflow.
    /// </summary>
    public static IReadOnlyList<int> StepNumbers(string list)
    {
        var numbers = new SortedSet<int>();

        foreach (Match match in StepItem.Matches(list))
        {
            var from = StepNumber(match.Groups["from"].Value);
            var to = match.Groups["to"].Success ? StepNumber(match.Groups["to"].Value) : from;

            if (to < from)
            {
                throw new FormatException($"step range '{match.Value}' goes backwards");
            }

            for (var step = from; step <= to; step++)
            {
                numbers.Add(step);
            }
        }

        return [.. numbers];
    }

    private static int StepNumber(string digits)
    {
        if (!int.TryParse(digits, out var number) || number > MaxStepNumber)
        {
            throw new FormatException($"step number {digits} is larger than {MaxStepNumber}");
        }

        return number;
    }

    private static bool IsOneOfSteps(string[] cells, IReadOnlyCollection<int> steps) =>
        StepNumberOf(cells[0]) is { } step && steps.Contains(step);

    private static string MarkedRow(string[] cells)
    {
        string[] marked = [$"{cells[0]} {OwnStepMark}", .. cells[1..]];

        return $"| {string.Join(" | ", marked)} |";
    }

    private static bool IsStepOf(string[] cells, string component) =>
        cells.Length > 1 && string.Equals(cells[1].Trim('`'), component, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<SpecStep> Parse(string specBlock, IReadOnlyCollection<string> codeComponents)
    {
        var section = MarkdownSections.Get(specBlock, Heading)
            ?? throw new FormatException($"'### {Heading}' section not found in the SPEC");

        var steps = new List<SpecStep>();
        var numbers = new HashSet<int>();

        foreach (var cells in MarkdownSections.TableRows(section))
        {
            // A "|" inside the free text of the Change cell must not fail the job: the extra cells join Change.
            if (cells.Length < 5)
            {
                throw new FormatException(
                    $"technical plan row must have 5 cells (Step | Component | File | Pattern from | Change), " +
                    $"got {cells.Length}: '{string.Join(" | ", cells)}'");
            }

            // The number in the Step cell names the step (the breakdown's Steps column uses it); a label without a
            // number falls back to the row's position. Used twice, Steps could not say which step it means.
            var step = StepNumberOf(cells[0]) ?? steps.Count + 1;
            var where = $"technical plan step {step}";
            if (!numbers.Add(step))
            {
                throw new FormatException($"{where} is used twice");
            }

            var component = ComponentNames.ParseOne(cells[1], codeComponents, where);
            var patternFrom = cells[3];
            var change = string.Join(" | ", cells.Skip(4));

            if (cells[2].Length == 0 || change.Length == 0)
            {
                throw new FormatException($"{where} has an empty file or change");
            }

            if (patternFrom.Length == 0)
            {
                throw new FormatException(
                    $"{where} has an empty Pattern from " +
                    "(name the existing file to copy, or `-` when no file of that kind exists)");
            }

            steps.Add(new SpecStep(step, component, cells[2], patternFrom, change));
        }

        if (steps.Count == 0)
        {
            throw new FormatException($"'### {Heading}' has no table rows");
        }

        return steps;
    }
}
