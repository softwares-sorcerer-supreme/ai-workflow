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

    public static IReadOnlyList<SpecStep> Parse(string specBlock, IReadOnlyCollection<string> codeComponents)
    {
        var section = MarkdownSections.Get(specBlock, Heading)
            ?? throw new FormatException($"'### {Heading}' section not found in the SPEC");

        var steps = new List<SpecStep>();

        foreach (var cells in MarkdownSections.TableRows(section))
        {
            // A "|" inside the free text of the Change cell must not fail the job: the extra cells join Change.
            if (cells.Length < 5)
            {
                throw new FormatException(
                    $"technical plan row must have 5 cells (Step | Component | File | Pattern from | Change), " +
                    $"got {cells.Length}: '{string.Join(" | ", cells)}'");
            }

            // The step number is only a label; its position is what counts.
            var step = steps.Count + 1;
            var where = $"technical plan step {step}";
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
