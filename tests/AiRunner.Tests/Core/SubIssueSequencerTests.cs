namespace AiRunner.Tests.Core;

public class SubIssueSequencerTests
{
    private static GbIssue Sub(int number, params string[] labels) => Samples.Issue(number, labels);

    private static GbPull Merged(int number, string head, string @base = "feat/issue-12") =>
        new(number, "t", new GbRef(head, "s"), new GbRef(@base, "b"), DateTimeOffset.UtcNow, State: "closed", Merged: true);

    private static readonly GbIssue[] Subs = [Sub(20, Labels.Simple, Labels.Implementing), Sub(21, Labels.Simple), Sub(22)];

    [Fact]
    public void First_sub_issue_is_started_by_a_human() =>
        Assert.Equal(SequenceStep.Wait, SubIssueSequencer.Decide(12, Subs, []).Step);

    [Fact]
    public void Next_one_starts_when_the_previous_pr_is_merged()
    {
        var d = SubIssueSequencer.Decide(12, Subs, [Merged(50, "ai/issue-12-1")]);
        Assert.Equal(SequenceStep.StartNext, d.Step);
        Assert.Equal((21, 20), (d.Next!.Number, d.Previous!.Number));
    }

    [Fact]
    public void Merge_into_another_branch_does_not_count() =>
        Assert.Equal(SequenceStep.Wait, SubIssueSequencer.Decide(12, Subs, [Merged(50, "ai/issue-12-1", "main")]).Step);

    [Fact]
    public void In_progress_sub_issue_is_not_started_twice()
    {
        GbIssue[] subs = [Sub(20), Sub(21, Labels.Simple, Labels.Implement), Sub(22)];
        var d = SubIssueSequencer.Decide(12, subs, [Merged(50, "ai/issue-12-1")]);
        Assert.Equal(SequenceStep.Wait, d.Step);
        Assert.Contains("in progress", d.Reason);
    }

    [Fact]
    public void Developer_sub_issue_waits_and_counts_as_done_when_closed()
    {
        var pulls = new[] { Merged(50, "ai/issue-12-1"), Merged(51, "ai/issue-12-2") };
        var waiting = SubIssueSequencer.Decide(12, Subs, pulls);
        Assert.Contains("developer", waiting.Reason);

        GbIssue[] closedThird = [Subs[0], Subs[1], Subs[2] with { State = "closed" }];
        Assert.Equal(SequenceStep.OpenFinalPr, SubIssueSequencer.Decide(12, closedThird, pulls).Step);
    }

    [Fact]
    public void Final_pr_is_opened_once()
    {
        var pulls = new List<GbPull> { Merged(50, "ai/issue-12-1"), Merged(51, "ai/issue-12-2"), Merged(52, "ai/issue-12-3") };
        Assert.Equal(SequenceStep.OpenFinalPr, SubIssueSequencer.Decide(12, Subs, pulls).Step);

        pulls.Add(new GbPull(60, "final", new GbRef("feat/issue-12", "s"), new GbRef("main", "b"), DateTimeOffset.UtcNow, State: "open"));
        Assert.Equal(SequenceStep.Wait, SubIssueSequencer.Decide(12, Subs, pulls).Step);
    }
}
