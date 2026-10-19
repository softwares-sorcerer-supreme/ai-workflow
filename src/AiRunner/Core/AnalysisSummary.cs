namespace AiRunner.Core;

/// <summary>What the analysis decided, read from the labels it set. Codes are <c>none</c> until it has run.</summary>
public sealed record AnalysisSummary(CodeLabel Status, CodeLabel Complexity)
{
    public const string None = "none";

    public static AnalysisSummary Of(IssueSnapshot issue)
    {
        CodeLabel status;
        if (issue.HasLabel(Labels.QuestionsOpen))
        {
            status = new CodeLabel("questions-open", "Questions open");
        }
        else if (issue.HasLabel(Labels.Ready))
        {
            status = new CodeLabel("ready", "Ready");
        }
        else
        {
            status = new CodeLabel(None, "Not analysed yet");
        }

        CodeLabel complexity;
        if (issue.HasLabel(Labels.Complex))
        {
            complexity = new CodeLabel("complex", "Complex");
        }
        else if (issue.HasLabel(Labels.Simple))
        {
            complexity = new CodeLabel("simple", "Simple");
        }
        else
        {
            complexity = new CodeLabel(None, "Unknown");
        }

        return new AnalysisSummary(status, complexity);
    }
}
