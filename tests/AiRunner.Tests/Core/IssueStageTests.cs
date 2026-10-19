using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

public sealed class IssueStageTests
{
    private const string Backend = "org/mbh-backend";
    private const string Frontend = "org/mbh-frontend";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Issues in org/mbh-issues, code in a backend and a frontend repo.</summary>
    private readonly ProjectConfig _project = TestProjects.SeparateIssuesRepo();

    private IssueSnapshot Row(GbIssue issue) => SnapshotRules.IssueRow(_project, issue, Now);

    private IssueSnapshot Open(int number, params string[] labels) => Row(Samples.Issue(number, labels));

    private IssueSnapshot Closed(int number, params string[] labels) =>
        Row(Samples.Issue(number, labels) with
        {
            State = "closed"
        });

    /// <summary>Sub-issue "order" of parent 12, built in the given component.</summary>
    private IssueSnapshot Sub(int number, int order, string component, bool isOpen = true)
    {
        var body = DeliveryLayouts.SubIssueBodyFor(12, order, component);
        var issue = Samples.Issue(number, Labels.SubIssue) with
        {
            Body = body,
            State = isOpen ? "open" : "closed"
        };

        return Row(issue);
    }

    private static GbPull Pull(string head, string baseRef = "main", string state = "open", bool isMerged = false) =>
        new(
            90,
            "t",
            new GbRef(head, "s"),
            new GbRef(baseRef, "b"),
            Now,
            State: isMerged ? "closed" : state,
            Merged: isMerged);

    private StageResult Compute(IssueSnapshot issue, params (string Repo, GbPull Pull)[] pulls) =>
        Compute(issue, [], pulls);

    private StageResult Compute(
        IssueSnapshot issue,
        IReadOnlyList<IssueSnapshot> subIssues,
        params (string Repo, GbPull Pull)[] pulls)
    {
        var pullsByRepo = pulls
            .GroupBy(entry => entry.Repo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GbPull>)group.Select(entry => entry.Pull).ToList(),
                StringComparer.OrdinalIgnoreCase);

        return IssueStage.Compute(new StageInput(_project, issue, subIssues, pullsByRepo));
    }

    // ---- one row of the stage table each, an open issue without a PR ----

    [Theory]
    [InlineData("new", "New")]
    [InlineData("questions-open", "Questions open", Labels.QuestionsOpen)]
    [InlineData("ready-simple", "Ready - simple", Labels.Ready, Labels.Simple)]
    [InlineData("ready-complex", "Ready - complex", Labels.Ready, Labels.Complex)]
    [InlineData("implementing", "Implementing", Labels.Implementing)]
    [InlineData("broken-down", "Broken down (0/0)", Labels.Parent)]
    public void Labels_decide_the_stage_of_an_open_issue_without_a_pr(string code, string label, params string[] labels)
    {
        var stage = Compute(Open(7, labels)).Stage;

        Assert.Equal((code, label), (stage.Code, stage.Label));
    }

    [Theory]
    [InlineData("implementing", Labels.Ready, Labels.Simple, Labels.Implementing)]
    [InlineData("broken-down", Labels.Ready, Labels.Complex, Labels.Parent)]
    [InlineData("ready-complex", Labels.Ready, Labels.Complex, Labels.QuestionsOpen)]
    [InlineData("new", Labels.Simple)]
    public void The_first_matching_rule_wins(string code, params string[] labels) =>
        Assert.Equal(code, Compute(Open(7, labels)).Stage.Code);

    [Fact]
    public void Open_issue_with_an_open_pr_is_pr_open_whatever_its_labels()
    {
        var pull = Pull("ai/issue-7");

        var stage = Compute(Open(7, Labels.Ready, Labels.Simple, Labels.Implementing), (Backend, pull)).Stage;

        Assert.Equal(("pr-open", "PR open"), (stage.Code, stage.Label));
    }

    [Fact]
    public void Merged_beats_an_open_pr_and_the_labels()
    {
        var open = Pull("ai/issue-7");
        var merged = Pull("ai/issue-7", isMerged: true);

        var stage = Compute(Open(7, Labels.Implementing), (Backend, open), (Backend, merged)).Stage;

        Assert.Equal(("merged", "Merged"), (stage.Code, stage.Label));
    }

    [Fact]
    public void Closed_issue_with_a_merged_pr_is_done()
    {
        var merged = Pull("ai/issue-7", isMerged: true);

        var stage = Compute(Closed(7, Labels.Implementing), (Frontend, merged)).Stage;

        Assert.Equal(("done", "Done"), (stage.Code, stage.Label));
    }

    [Fact]
    public void Closed_issue_without_a_merged_pr_is_closed_no_merge()
    {
        var closedPull = Pull("ai/issue-7", state: "closed");

        var stage = Compute(Closed(7, Labels.Implementing), (Backend, closedPull)).Stage;

        Assert.Equal(("closed-no-merge", "Closed (no merge)"), (stage.Code, stage.Label));
    }

    [Fact]
    public void Pr_closed_without_a_merge_leaves_the_labels_to_decide()
    {
        var closedPull = Pull("ai/issue-7", state: "closed");

        Assert.Equal("implementing", Compute(Open(7, Labels.Implementing), (Backend, closedPull)).Stage.Code);
    }

    [Fact]
    public void Pr_of_another_issue_or_a_human_branch_does_not_count()
    {
        var otherIssue = Pull("ai/issue-70");
        var humanBranch = Pull("feature/issue-7");

        Assert.Equal("new", Compute(Open(7), (Backend, otherIssue), (Backend, humanBranch)).Stage.Code);
    }

    // ---- sub-issues: the slot PR in the repo of the component ----

    [Fact]
    public void Sub_issue_pr_counts_in_the_repo_of_its_component_only()
    {
        var subIssue = Sub(21, 2, "frontend");
        var slotPull = Pull("ai/issue-12-2", "feat/issue-12");

        Assert.Equal("pr-open", Compute(subIssue, (Frontend, slotPull)).Stage.Code);
        Assert.Equal("new", Compute(subIssue, (Backend, slotPull)).Stage.Code);
    }

    [Fact]
    public void Sub_issue_pr_into_another_base_does_not_count()
    {
        var subIssue = Sub(21, 2, "frontend");
        var intoMain = Pull("ai/issue-12-2", "main", isMerged: true);

        Assert.Equal("new", Compute(subIssue, (Frontend, intoMain)).Stage.Code);
    }

    [Fact]
    public void Sub_issue_merged_into_the_feature_branch_and_closed_is_done()
    {
        var subIssue = Sub(21, 2, "frontend", isOpen: false);
        var merged = Pull("ai/issue-12-2", "feat/issue-12", isMerged: true);

        Assert.Equal("done", Compute(subIssue, (Frontend, merged)).Stage.Code);
    }

    // ---- parents: n/m and the final PRs, one per repo that has sub-issues ----

    [Fact]
    public void Parent_shows_closed_of_all_sub_issues()
    {
        IReadOnlyList<IssueSnapshot> subIssues =
            [Sub(20, 1, "backend", isOpen: false), Sub(21, 2, "frontend"), Sub(22, 3, "frontend")];

        var stage = Compute(Open(12, Labels.Parent, Labels.Ready, Labels.Complex), subIssues).Stage;

        Assert.Equal(("broken-down", "Broken down (1/3)"), (stage.Code, stage.Label));
    }

    /// <summary>Both sub-issues of parent 12 are done: one in the backend repo, one in the frontend repo.</summary>
    private IReadOnlyList<IssueSnapshot> DoneInTwoRepos() =>
        [Sub(20, 1, "backend", isOpen: false), Sub(21, 2, "frontend", isOpen: false)];

    [Fact]
    public void Parent_with_one_final_pr_merged_and_one_open_is_pr_open()
    {
        var subIssues = DoneInTwoRepos();
        var backend = (Backend, Pull("feat/issue-12", isMerged: true));
        var frontend = (Frontend, Pull("feat/issue-12"));

        var result = Compute(Open(12, Labels.Parent), subIssues, backend, frontend);

        Assert.Equal("pr-open", result.Stage.Code);
    }

    [Fact]
    public void Parent_is_merged_only_when_every_final_pr_is_merged()
    {
        var subIssues = DoneInTwoRepos();
        var backend = (Backend, Pull("feat/issue-12", isMerged: true));
        var frontend = (Frontend, Pull("feat/issue-12", isMerged: true));

        var openParent = Compute(Open(12, Labels.Parent), subIssues, backend, frontend);
        var closedParent = Compute(Closed(12, Labels.Parent), subIssues, backend, frontend);
        var oneMissing = Compute(Closed(12, Labels.Parent), subIssues, backend);

        Assert.Equal("merged", openParent.Stage.Code);
        Assert.Equal("done", closedParent.Stage.Code);
        Assert.Equal("closed-no-merge", oneMissing.Stage.Code);
    }

    [Fact]
    public void Parent_ignores_an_ai_issue_pr_of_its_own_number()
    {
        IReadOnlyList<IssueSnapshot> subIssues = [Sub(20, 1, "backend")];
        var simplePull = Pull("ai/issue-12");

        Assert.Equal("broken-down", Compute(Open(12, Labels.Parent), subIssues, (Backend, simplePull)).Stage.Code);
    }

    // ---- flags and badge ----

    [Fact]
    public void Flags_show_failed_claimed_and_on_github()
    {
        var issue = Open(7, Labels.Failed, Labels.Claimed, Labels.OnGitHub);

        var flags = Compute(issue).Flags;

        Assert.Equal(
            [("failed", "Failed"), ("claimed", "Claimed by another runner"), ("on-github", "On GitHub")],
            flags.Select(flag => (flag.Code, flag.Label)));
    }

    [Fact]
    public void Needs_cloud_agent_is_on_github_too() =>
        Assert.Equal("on-github", Assert.Single(Compute(Open(7, Labels.NeedsCloudAgent)).Flags).Code);

    [Fact]
    public void Claim_names_this_runner_only_while_its_job_runs()
    {
        var issue = Open(7, Labels.Claimed);
        var input = new StageInput(_project, issue, [], new Dictionary<string, IReadOnlyList<GbPull>>())
        {
            RunnerName = "laptop-1",
        };

        var running = IssueStage.Compute(input with
        {
            Jobs = new JobActivity(IsQueued: false, IsRunning: true)
        });
        var queued = IssueStage.Compute(input with
        {
            Jobs = new JobActivity(IsQueued: true, IsRunning: false)
        });

        Assert.Equal("Claimed by laptop-1", Assert.Single(running.Flags).Label);
        Assert.Equal("Claimed by another runner", Assert.Single(queued.Flags).Label);
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, "queued")]
    [InlineData(false, true, "running")]
    [InlineData(true, true, "running")]
    public void Badge_comes_from_this_runners_jobs(bool isQueued, bool isRunning, string? badge)
    {
        var input = new StageInput(_project, Open(7), [], new Dictionary<string, IReadOnlyList<GbPull>>())
        {
            Jobs = new JobActivity(isQueued, isRunning),
        };

        Assert.Equal(badge, IssueStage.Compute(input).Badge?.Code);
    }

    [Fact]
    public void Every_stage_code_of_the_spec_is_listed_once_in_rule_order() =>
        Assert.Equal(
            ["done", "closed-no-merge", "merged", "pr-open", "broken-down", "implementing", "ready-complex",
             "ready-simple", "questions-open", "new"],
            IssueStage.Stages.Select(stage => stage.Code));
}
