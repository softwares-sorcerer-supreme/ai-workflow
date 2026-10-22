namespace AiRunner.Tests.Core;

public class SubIssueSequencerTests
{
    private const string Backend = "org/backend";
    private const string Frontend = "org/frontend";

    private static HostIssue Sub(int number, params string[] labels) => Samples.Issue(number, labels);

    private static HostPull Merged(int number, string head, string @base = "feat/issue-12") =>
        new(
            number,
            "t",
            new HostRef(head, "s"),
            new HostRef(@base, "b"),
            DateTimeOffset.UtcNow,
            State: "closed",
            Merged: true);

    private static HostPull FinalPull(int number, bool isMerged) =>
        new(
            number,
            "final",
            new HostRef("feat/issue-12", "s"),
            new HostRef("main", "b"),
            DateTimeOffset.UtcNow,
            State: isMerged ? "closed" : "open",
            Merged: isMerged);

    private static readonly HostIssue[] Subs =
        [Sub(20, Labels.Simple, Labels.Implementing), Sub(21, Labels.Simple), Sub(22)];

    /// <summary>All sub-issues in one repo, the PRs of that repo.</summary>
    private static SequenceInput OneRepo(IReadOnlyList<HostIssue> subIssues, params HostPull[] pulls) =>
        Input(subIssues.Select(subIssue => new SubIssueRef(subIssue, Backend)).ToList(), (Backend, pulls));

    private static SequenceInput Input(
        IReadOnlyList<SubIssueRef> subIssues,
        params (string Repo, HostPull[] Pulls)[] pullsByRepo)
    {
        var pulls = pullsByRepo.ToDictionary(
            entry => entry.Repo,
            entry => (IReadOnlyList<HostPull>)entry.Pulls,
            StringComparer.OrdinalIgnoreCase);

        return new SequenceInput(12, subIssues, pulls);
    }

    [Fact]
    public void First_sub_issue_is_started_by_a_human() =>
        Assert.Equal(SequenceStep.Wait, SubIssueSequencer.Decide(OneRepo(Subs)).Step);

    [Fact]
    public void Next_one_starts_when_the_previous_pr_is_merged()
    {
        var mergedPull = Merged(50, "ai/issue-12-1");
        var decision = SubIssueSequencer.Decide(OneRepo(Subs, mergedPull));

        Assert.Equal(SequenceStep.StartNext, decision.Step);
        Assert.Equal((21, 20), (decision.Next!.Number, decision.Previous!.Number));
    }

    [Fact]
    public void Merge_into_another_branch_does_not_count()
    {
        var mergedElsewhere = Merged(50, "ai/issue-12-1", "main");

        Assert.Equal(SequenceStep.Wait, SubIssueSequencer.Decide(OneRepo(Subs, mergedElsewhere)).Step);
    }

    [Fact]
    public void In_progress_sub_issue_is_not_started_twice()
    {
        HostIssue[] subIssues = [Sub(20), Sub(21, Labels.Simple, Labels.Implement), Sub(22)];
        var mergedPull = Merged(50, "ai/issue-12-1");
        var decision = SubIssueSequencer.Decide(OneRepo(subIssues, mergedPull));

        Assert.Equal(SequenceStep.Wait, decision.Step);
        Assert.Contains("in progress", decision.Reason);
    }

    [Fact]
    public void A_failed_sub_issue_is_not_started_again()
    {
        HostIssue[] subIssues = [Sub(20), Sub(21, Labels.Simple, Labels.Failed), Sub(22)];
        var decision = SubIssueSequencer.Decide(OneRepo(subIssues, Merged(50, "ai/issue-12-1")));

        Assert.Equal(SequenceStep.Wait, decision.Step);
    }

    private static HostPull Pull(int number, string head, string state) =>
        new(
            number,
            "t",
            new HostRef(head, "s"),
            new HostRef("feat/issue-12", "b"),
            DateTimeOffset.UtcNow,
            State: state,
            Merged: false);

    [Fact]
    public void A_pr_closed_without_a_merge_stops_an_implementing_sub_issue()
    {
        var input = OneRepo(Subs, Pull(50, "ai/issue-12-1", "closed"));

        var abandoned = SubIssueSequencer.ClosedWithoutMerge(input);

        Assert.Equal([20], abandoned.Select(entry => entry.SubIssue.Issue.Number));
        Assert.Equal([50], abandoned.Select(entry => entry.Pull.Number));
    }

    [Fact]
    public void An_open_or_merged_pr_on_the_slot_is_not_abandoned()
    {
        var closed = Pull(50, "ai/issue-12-1", "closed");
        var reopened = Pull(51, "ai/issue-12-1", "open");

        Assert.Empty(SubIssueSequencer.ClosedWithoutMerge(OneRepo(Subs, closed, reopened)));
        Assert.Empty(SubIssueSequencer.ClosedWithoutMerge(OneRepo(Subs, closed, Merged(52, "ai/issue-12-1"))));
        Assert.Empty(SubIssueSequencer.ClosedWithoutMerge(OneRepo(Subs)));
    }

    [Fact]
    public void A_sub_issue_without_ai_implementing_is_not_abandoned()
    {
        HostIssue[] subIssues = [Sub(20, Labels.Simple), Sub(21, Labels.Simple), Sub(22)];

        Assert.Empty(SubIssueSequencer.ClosedWithoutMerge(OneRepo(subIssues, Pull(50, "ai/issue-12-1", "closed"))));
    }

    [Fact]
    public void Developer_sub_issue_waits_and_counts_as_done_when_closed()
    {
        var pulls = new[] { Merged(50, "ai/issue-12-1"), Merged(51, "ai/issue-12-2") };
        var waiting = SubIssueSequencer.Decide(OneRepo(Subs, pulls));

        Assert.Contains("developer", waiting.Reason);

        HostIssue[] closedThird = [Subs[0], Subs[1], Subs[2] with { State = "closed" }];
        Assert.Equal(SequenceStep.OpenFinalPrs, SubIssueSequencer.Decide(OneRepo(closedThird, pulls)).Step);
    }

    [Fact]
    public void A_pr_merged_in_the_repo_of_the_sub_issue_counts_and_one_in_another_repo_does_not()
    {
        SubIssueRef[] subIssues =
        [
            new(Sub(20, Labels.Simple), Backend),
            new(Sub(21, Labels.Simple), Frontend),
            new(Sub(22), Backend),
        ];
        var mergedBackend = Merged(50, "ai/issue-12-1");

        var counted = SubIssueSequencer.Decide(Input(subIssues, (Backend, [mergedBackend]), (Frontend, [])));
        var wrongRepo = SubIssueSequencer.Decide(Input(subIssues, (Backend, []), (Frontend, [mergedBackend])));

        Assert.Equal((SequenceStep.StartNext, 21), (counted.Step, counted.Next!.Number));
        Assert.Equal(SequenceStep.Wait, wrongRepo.Step);
    }

    [Fact]
    public void A_merged_pr_whose_sub_issue_is_still_open_is_listed_to_be_closed()
    {
        SubIssueRef[] subIssues =
        [
            new(Sub(20, Labels.Simple, Labels.Implementing), Backend),
            new(Sub(21, Labels.Simple), Frontend),
            new(Sub(22) with { State = "closed" }, Frontend),
        ];
        var input = Input(
            subIssues,
            (Backend, [Merged(50, "ai/issue-12-1")]),
            (Frontend, [Merged(51, "ai/issue-12-2"), Merged(52, "ai/issue-12-3")]));

        var merged = SubIssueSequencer.MergedButOpen(input);

        Assert.Equal([20, 21], merged.Select(entry => entry.SubIssue.Issue.Number));
        Assert.Equal([50, 51], merged.Select(entry => entry.Pull.Number));
    }

    [Fact]
    public void A_final_pr_is_opened_in_each_repo_that_has_sub_issues_and_only_where_it_is_missing()
    {
        SubIssueRef[] subIssues =
        [
            new(Sub(20) with { State = "closed" }, Backend),
            new(Sub(21) with { State = "closed" }, Frontend),
            new(Sub(22) with { State = "closed" }, Backend),
        ];

        var none = SubIssueSequencer.Decide(Input(subIssues, (Backend, []), (Frontend, [])));
        var oneThere = SubIssueSequencer.Decide(Input(subIssues, (Backend, [FinalPull(60, false)]), (Frontend, [])));

        Assert.Equal(SequenceStep.OpenFinalPrs, none.Step);
        Assert.Equal([Backend, Frontend], none.Repos);
        Assert.Equal([Frontend], oneThere.Repos);
    }

    [Fact]
    public void The_list_comment_is_still_due_when_every_final_pr_exists_but_the_comment_does_not()
    {
        SubIssueRef[] subIssues = [new(Sub(20) with { State = "closed" }, Backend)];
        var pulls = (Backend, new[] { FinalPull(60, false) });

        var withoutList = SubIssueSequencer.Decide(Input(subIssues, pulls));
        var withList = SubIssueSequencer.Decide(Input(subIssues, pulls) with { HasFinalList = true });

        Assert.Equal((SequenceStep.OpenFinalPrs, 0), (withoutList.Step, withoutList.Repos.Count));
        Assert.Equal(SequenceStep.Wait, withList.Step);
    }

    [Fact]
    public void The_parent_is_closed_only_when_every_final_pr_is_merged()
    {
        SubIssueRef[] subIssues =
        [
            new(Sub(20) with { State = "closed" }, Backend),
            new(Sub(21) with { State = "closed" }, Frontend),
        ];
        var oneOpen = Input(subIssues, (Backend, [FinalPull(60, true)]), (Frontend, [FinalPull(61, false)]))
            with { HasFinalList = true };
        var bothMerged = Input(subIssues, (Backend, [FinalPull(60, true)]), (Frontend, [FinalPull(61, true)]))
            with { HasFinalList = true };

        Assert.Equal(SequenceStep.Wait, SubIssueSequencer.Decide(oneOpen).Step);
        Assert.Equal(SequenceStep.CloseParent, SubIssueSequencer.Decide(bothMerged).Step);
    }
}
