namespace AiRunner.Core;

/// <summary>One step of the technical plan in a SPEC: which component, which file (relative to the component folder), what changes.</summary>
public sealed record SpecStep(int Step, string Component, string File, string Change);

/// <summary>
/// Checks "### Technical plan" in a SPEC block (templates/spec.md): a table | Step | Component | File | Change |
/// where every Component is one code component of the project. Other sections are free text and not checked here.
/// A bad SPEC fails the job like a bad Status would.
/// </summary>
public static class SpecParser
{
    private const string Heading = "Technical plan";

    public static IReadOnlyList<SpecStep> Parse(string specBlock, IReadOnlyCollection<string> codeComponents)
    {
        var section = MarkdownSections.Get(specBlock, Heading)
            ?? throw new FormatException($"'### {Heading}' section not found in the SPEC");

        var steps = new List<SpecStep>();

        foreach (var cells in MarkdownSections.TableRows(section))
        {
            // A "|" inside the free text of the Change cell must not fail the job: the extra cells join Change.
            if (cells.Length < 4)
            {
                throw new FormatException(
                    $"technical plan row must have 4 cells (Step | Component | File | Change), got {cells.Length}: " +
                    $"'{string.Join(" | ", cells)}'");
            }

            // The step number is only a label; its position is what counts.
            var step = steps.Count + 1;
            var where = $"technical plan step {step}";
            var component = ComponentNames.ParseOne(cells[1], codeComponents, where);
            var change = string.Join(" | ", cells.Skip(3));

            if (cells[2].Length == 0 || change.Length == 0)
            {
                throw new FormatException($"{where} has an empty file or change");
            }

            steps.Add(new SpecStep(step, component, cells[2], change));
        }

        if (steps.Count == 0)
        {
            throw new FormatException($"'### {Heading}' has no table rows");
        }

        return steps;
    }
}
