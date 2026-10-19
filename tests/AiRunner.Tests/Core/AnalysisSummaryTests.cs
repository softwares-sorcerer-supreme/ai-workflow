using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

public sealed class AnalysisSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static AnalysisSummary Summary(params string[] labels)
    {
        var issue = SnapshotRules.IssueRow(TestProjects.SeparateIssuesRepo(), Samples.Issue(7, labels), Now);

        return AnalysisSummary.Of(issue);
    }

    [Theory]
    [InlineData("none", "none")]
    [InlineData("questions-open", "none", Labels.QuestionsOpen)]
    [InlineData("ready", "simple", Labels.Ready, Labels.Simple)]
    [InlineData("ready", "complex", Labels.Ready, Labels.Complex)]
    [InlineData("questions-open", "complex", Labels.QuestionsOpen, Labels.Ready, Labels.Complex)]
    public void Labels_decide_status_and_complexity(string status, string complexity, params string[] labels)
    {
        var summary = Summary(labels);

        Assert.Equal((status, complexity), (summary.Status.Code, summary.Complexity.Code));
    }
}
