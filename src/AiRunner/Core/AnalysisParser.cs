namespace AiRunner.Core;

public enum AnalysisStatus
{
    QuestionsOpen, Ready
}

public enum Complexity
{
    Simple, Complex
}

public sealed record AnalysisResult(
    AnalysisStatus Status,
    Complexity Complexity,
    IReadOnlyList<string> LabelsToAdd,
    IReadOnlyList<string> LabelsToRemove)
{
    /// <summary>Code components the issue touches. Empty only while questions are open and the agent cannot say yet.</summary>
    public IReadOnlyList<string> Components { get; init; } = [];
}

/// <summary>
/// Reads Status, Complexity and Components affected from an analysis block (Template F) and turns them into
/// labels. Fixed vocabularies: anything else fails the job, so a bad agent answer is never half-applied.
/// An issue that touches more than one component is never simple: it is split into one sub-issue per component.
/// </summary>
public static class AnalysisParser
{
    private const string ComponentsField = "Components affected";

    public static AnalysisResult Parse(string block, IReadOnlyCollection<string> codeComponents)
    {
        var status = MarkdownSections.ReadField(block, "Status") switch
        {
            "questions-open" => AnalysisStatus.QuestionsOpen,
            "ready" => AnalysisStatus.Ready,
            var value => throw new FormatException($"Status must be 'questions-open' or 'ready', got '{value}'"),
        };

        var complexity = MarkdownSections.ReadField(block, "Complexity") switch
        {
            "simple" => Complexity.Simple,
            "complex" => Complexity.Complex,
            var value => throw new FormatException($"Complexity must be 'simple' or 'complex', got '{value}'"),
        };

        var isReady = status == AnalysisStatus.Ready;
        var componentsText = MarkdownSections.ReadField(block, ComponentsField);
        var components = ComponentNames.ParseList(componentsText, codeComponents, mayBeUnknown: !isReady);

        if (components.Count > 1 && complexity == Complexity.Simple)
        {
            throw new FormatException(
                $"Complexity is 'simple' but {ComponentsField} lists {components.Count} components " +
                $"({string.Join(", ", components)}); an issue that touches more than one component is 'complex'");
        }

        if (!isReady)
        {
            return new(status, complexity, [Labels.QuestionsOpen], [Labels.Ready, Labels.Simple, Labels.Complex])
            {
                Components = components,
            };
        }

        var (labelToAdd, labelToRemove) = complexity == Complexity.Simple
            ? (Labels.Simple, Labels.Complex)
            : (Labels.Complex, Labels.Simple);

        return new(status, complexity, [Labels.Ready, labelToAdd], [Labels.QuestionsOpen, labelToRemove])
        {
            Components = components,
        };
    }

    /// <summary>
    /// The components an analysis block names, or an empty list when it names none that are valid. Later jobs use it
    /// to find the target component of an issue; an analysis written before components existed has no such line,
    /// and that is not an error here (the job falls back to the only code component, or asks for a new analysis).
    /// </summary>
    public static IReadOnlyList<string> ComponentsOf(string block, IReadOnlyCollection<string> codeComponents)
    {
        try
        {
            var componentsText = MarkdownSections.ReadField(block, ComponentsField);

            return ComponentNames.ParseList(componentsText, codeComponents, mayBeUnknown: true);
        }
        catch (FormatException)
        {
            // No line, or a name that is no code component: the caller treats it as "not named".
            return [];
        }
    }
}
