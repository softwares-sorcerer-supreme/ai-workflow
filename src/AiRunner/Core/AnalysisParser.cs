namespace AiRunner.Core;

public enum AnalysisStatus { QuestionsOpen, Ready }

public enum Complexity { Simple, Complex }

public sealed record AnalysisResult(
    AnalysisStatus Status,
    Complexity Complexity,
    IReadOnlyList<string> LabelsToAdd,
    IReadOnlyList<string> LabelsToRemove);

/// <summary>
/// Reads Status and Complexity from an analysis block (Template F) and turns them into
/// labels. Fixed vocabularies: anything else fails the job, so a bad agent answer is
/// never half-applied.
/// </summary>
public static class AnalysisParser
{
    public static AnalysisResult Parse(string block)
    {
        var status = MarkdownSections.ReadField(block, "Status") switch
        {
            "questions-open" => AnalysisStatus.QuestionsOpen,
            "ready" => AnalysisStatus.Ready,
            var v => throw new FormatException($"Status must be 'questions-open' or 'ready', got '{v}'"),
        };
        var complexity = MarkdownSections.ReadField(block, "Complexity") switch
        {
            "simple" => Complexity.Simple,
            "complex" => Complexity.Complex,
            var v => throw new FormatException($"Complexity must be 'simple' or 'complex', got '{v}'"),
        };

        if (status == AnalysisStatus.QuestionsOpen)
            return new(status, complexity, [Labels.QuestionsOpen], [Labels.Ready, Labels.Simple, Labels.Complex]);

        var (add, other) = complexity == Complexity.Simple
            ? (Labels.Simple, Labels.Complex)
            : (Labels.Complex, Labels.Simple);
        return new(status, complexity, [Labels.Ready, add], [Labels.QuestionsOpen, other]);
    }
}
