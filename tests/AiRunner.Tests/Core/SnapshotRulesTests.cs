using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

public sealed class SnapshotRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ProjectConfig _project = TestProjects.SeparateIssuesRepo();

    private IssueSnapshot Row(GbIssue issue) => SnapshotRules.IssueRow(_project, issue, Now);

    private static GbIssue UpdatedDaysAgo(GbIssue issue, int days) =>
        issue with
        {
            UpdatedAt = Now.AddDays(-days),
            State = "closed"
        };

    private static RetentionWindow Window(params int[] openNumbers) => new(Now, 30, openNumbers.ToHashSet());

    /// <summary>A closed PR in the backend repo that links issue #7 of the issues repo.</summary>
    private PullSnapshot PullRow(string head)
    {
        var pull = new GbPull(
            5,
            "t",
            new GbRef(head, "s"),
            new GbRef("main", "b"),
            Now,
            "Issue: org/mbh-issues#7",
            State: "closed");

        return SnapshotRules.PullRow(new PullSource(_project, "org/mbh-backend", Now), pull);
    }

    [Fact]
    public void Issue_row_takes_parent_order_and_component_from_a_sub_issue_body()
    {
        var body = DeliveryLayouts.SubIssueBodyFor(12, 2, "frontend");
        var issue = Samples.Issue(21, Labels.SubIssue, "ai") with
        {
            Body = body,
            Assignee = new GbUser("bot"),
            HtmlUrl = "http://gb/org/mbh-issues/issues/21"
        };

        var row = Row(issue);

        Assert.Equal(("org/mbh-issues", "mbh", "open"), (row.Repo, row.Project, row.State));
        Assert.Equal((12, 2, "frontend"), (row.Parent, row.SubOrder, row.Component));
        Assert.Equal(["ai", Labels.SubIssue], row.Labels);
        Assert.Equal(["bot"], row.Assignees);
        Assert.Equal("http://gb/org/mbh-issues/issues/21", row.HtmlUrl);
    }

    [Fact]
    public void Broken_sub_issue_body_gives_a_plain_row_instead_of_an_error()
    {
        var issue = Samples.Issue(21) with
        {
            Body = "Parent: #12\nno order line"
        };

        var row = Row(issue);

        Assert.Null(row.Parent);
        Assert.Null(row.SubOrder);
    }

    [Fact]
    public void Closed_issue_is_kept_while_it_is_recent()
    {
        var recent = Row(UpdatedDaysAgo(Samples.Issue(7), 29));
        var old = Row(UpdatedDaysAgo(Samples.Issue(8), 31));

        Assert.True(SnapshotRules.KeepClosedIssue(recent, Window()));
        Assert.False(SnapshotRules.KeepClosedIssue(old, Window()));
    }

    [Fact]
    public void Old_closed_sub_issue_is_kept_while_its_parent_is_open()
    {
        var body = DeliveryLayouts.SubIssueBodyFor(12, 1, "backend");
        var subIssue = Row(UpdatedDaysAgo(Samples.Issue(20) with { Body = body }, 90));

        Assert.True(SnapshotRules.KeepClosedIssue(subIssue, Window(12)));
        Assert.False(SnapshotRules.KeepClosedIssue(subIssue, Window(13)));
    }

    [Theory]
    [InlineData("ai/issue-7", true)]
    [InlineData("ai/issue-12-2", true)]
    [InlineData("feat/issue-12", true)]
    [InlineData("ai/pr-30-fix-1", true)]
    [InlineData("feature/login", false)]
    [InlineData("ai/other", false)]
    public void Closed_pr_is_kept_only_from_a_runner_branch(string head, bool isKept)
    {
        var row = PullRow(head);
        var keys = new HashSet<string>(["org/mbh-issues#7"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(isKept, SnapshotRules.KeepClosedPull(row, keys));
    }

    [Fact]
    public void Closed_pr_is_dropped_when_its_linked_issue_is_not_in_the_snapshot()
    {
        var row = PullRow("ai/issue-7");
        var keys = new HashSet<string>(["org/mbh-issues#8"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal("org/mbh-issues#7", row.LinkedIssue);
        Assert.False(SnapshotRules.KeepClosedPull(row, keys));
    }

    [Fact]
    public void Component_of_a_simple_issue_is_the_only_code_component()
    {
        var single = TestProjects.Single();
        var simple = SnapshotRules.IssueRow(single, Samples.Issue(7), Now);

        Assert.Equal("r", SnapshotRules.ComponentOf(simple, single));
        Assert.Null(SnapshotRules.ComponentOf(Row(Samples.Issue(7)), _project));
    }
}
