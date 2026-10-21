using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

/// <summary>Table tests of <see cref="IssueActions"/>: what is offered, what is not, and the write of each action.</summary>
public sealed class IssueActionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly CodeLabel FailedFlag = new(IssueMarks.Failed, "Failed");
    private static readonly CodeLabel QueuedBadge = new(IssueMarks.Queued, "Queued");
    private static readonly CodeLabel RunningBadge = new(IssueMarks.Running, "Running");
    private static readonly CodeLabel OnGitHubFlag = new(IssueMarks.OnGitHub, "On GitHub");

    private static ProjectSettings Settings(params string[] workflows) =>
        new() { Workflows = workflows.Length == 0 ? [.. Workflows.All] : [.. workflows] };

    private static IssueSnapshot Issue(int number = 7, bool isOpen = true, params string[] labels) =>
        SnapshotRules.IssueRow(TestProjects.SeparateIssuesRepo(), Samples.Issue(number, labels) with
        {
            State = isOpen ? "open" : "closed"
        }, Now);

    private static StageResult Stage(string code, CodeLabel? flag = null, CodeLabel? badge = null)
    {
        var label = IssueStage.Stages.First(stage => stage.Code == code);
        IReadOnlyList<CodeLabel> flags = flag is null ? [] : [flag];

        return new StageResult(label, flags, badge);
    }

    private static IReadOnlyList<string> CodesOf(
        IssueSnapshot issue,
        StageResult stage,
        ProjectSettings? settings = null,
        IReadOnlyList<SubIssueProgress>? progress = null,
        IReadOnlyList<IssueSnapshot>? subIssues = null)
    {
        var waiting = new WaitingInput(issue, stage, false, progress ?? []);
        var input = new IssueActionInput(waiting, settings ?? Settings(), subIssues ?? []);

        return IssueActions.Available(input)
            .Select(action => action.Code)
            .ToList();
    }

    // ---- one case per row of the spec ----

    [Fact]
    public void Ready_simple_without_the_label_offers_implement()
    {
        var codes = CodesOf(Issue(), Stage(IssueStage.ReadySimple));

        Assert.Equal([ActionCodes.Implement, ActionCodes.Ask], codes);
    }

    [Fact]
    public void Ready_complex_without_the_label_offers_breakdown()
    {
        var codes = CodesOf(Issue(), Stage(IssueStage.ReadyComplex));

        Assert.Equal([ActionCodes.Breakdown, ActionCodes.Ask], codes);
    }

    [Fact]
    public void A_failed_issue_offers_only_retry()
    {
        var codes = CodesOf(Issue(labels: Labels.Failed), Stage(IssueStage.ReadySimple, FailedFlag));

        Assert.Equal([ActionCodes.Retry], codes);
    }

    [Fact]
    public void Open_questions_offer_answer_and_not_ask()
    {
        var codes = CodesOf(Issue(), Stage(IssueStage.QuestionsOpen));

        Assert.Equal([ActionCodes.Answer], codes);
    }

    [Fact]
    public void A_new_issue_offers_ask()
    {
        var codes = CodesOf(Issue(), Stage(IssueStage.New));

        Assert.Equal([ActionCodes.Ask], codes);
    }

    [Fact]
    public void Broken_down_with_nothing_in_progress_offers_the_next_sub_issue()
    {
        var subIssues = new[] { Issue(20, labels: [Labels.Ready, Labels.Simple]), Issue(21) };
        var progress = new[]
        {
            new SubIssueProgress(true, IssueStage.New, false),
            new SubIssueProgress(true, IssueStage.New, false),
        };

        var codes = CodesOf(Issue(labels: Labels.Parent), Stage(IssueStage.BrokenDown), null, progress, subIssues);
        var next = IssueActions.NextSubIssueToStart(new IssueActionInput(
            new WaitingInput(Issue(), Stage(IssueStage.BrokenDown), false, progress), Settings(), subIssues));

        Assert.Equal([ActionCodes.StartNextSubIssue], codes);
        Assert.Equal(20, next?.Number);
    }

    // ---- not offered ----

    [Fact]
    public void Nothing_is_offered_on_a_closed_issue()
    {
        var codes = CodesOf(Issue(isOpen: false), Stage(IssueStage.ReadySimple));

        Assert.Empty(codes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nothing_is_offered_while_a_job_is_queued_or_running(bool isQueued)
    {
        var badge = isQueued ? QueuedBadge : RunningBadge;

        var codes = CodesOf(Issue(), Stage(IssueStage.ReadySimple, badge: badge));
        var failedCodes = CodesOf(Issue(), Stage(IssueStage.ReadySimple, FailedFlag, badge));

        Assert.Empty(codes);
        Assert.Empty(failedCodes);
    }

    [Fact]
    public void Implement_is_not_offered_when_the_label_is_already_there()
    {
        var codes = CodesOf(Issue(labels: Labels.Implement), Stage(IssueStage.ReadySimple));

        Assert.Empty(codes);
    }

    [Fact]
    public void Breakdown_is_not_offered_when_the_label_is_already_there()
    {
        var codes = CodesOf(Issue(labels: Labels.Breakdown), Stage(IssueStage.ReadyComplex));

        Assert.Empty(codes);
    }

    [Fact]
    public void A_workflow_that_is_off_hides_its_actions()
    {
        var implement = CodesOf(Issue(), Stage(IssueStage.ReadySimple), Settings(Workflows.Review));
        var breakdown = CodesOf(Issue(), Stage(IssueStage.ReadyComplex), Settings(Workflows.Implement));
        var analyze = CodesOf(Issue(), Stage(IssueStage.QuestionsOpen), Settings(Workflows.Implement));

        Assert.Empty(implement);
        Assert.Empty(breakdown);
        Assert.Empty(analyze);
    }

    [Fact]
    public void A_cloud_fallback_issue_is_offered_nothing()
    {
        var codes = CodesOf(Issue(labels: Labels.OnGitHub), Stage(IssueStage.ReadySimple, OnGitHubFlag));

        Assert.Empty(codes);
    }

    [Fact]
    public void The_next_sub_issue_is_not_offered_when_one_is_in_progress_or_none_is_open()
    {
        var subIssues = new[] { Issue(20), Issue(21) };
        var busy = new[]
        {
            new SubIssueProgress(true, IssueStage.Implementing, false),
            new SubIssueProgress(true, IssueStage.New, false),
        };
        var allClosed = new[]
        {
            new SubIssueProgress(false, IssueStage.Done, false),
            new SubIssueProgress(false, IssueStage.Done, false),
        };

        var inProgress = CodesOf(Issue(), Stage(IssueStage.BrokenDown), null, busy, subIssues);
        var none = CodesOf(Issue(), Stage(IssueStage.BrokenDown), null, allClosed, subIssues);

        Assert.Empty(inProgress);
        Assert.Empty(none);
    }

    [Fact]
    public void The_next_sub_issue_is_not_offered_when_that_sub_issue_failed_or_implement_is_off()
    {
        var progress = new[] { new SubIssueProgress(true, IssueStage.New, false) };
        var failedSub = new[] { Issue(20, labels: Labels.Failed) };
        var openSub = new[] { Issue(20) };

        var failed = CodesOf(Issue(), Stage(IssueStage.BrokenDown), null, progress, failedSub);
        var off = CodesOf(Issue(), Stage(IssueStage.BrokenDown), Settings(Workflows.Analyze), progress, openSub);

        Assert.Empty(failed);
        Assert.Empty(off);
    }

    [Fact]
    public void Ask_is_not_offered_where_the_planner_would_not_analyze()
    {
        var parent = CodesOf(Issue(labels: Labels.Parent), Stage(IssueStage.BrokenDown));
        var implementing = CodesOf(Issue(), Stage(IssueStage.Implementing));
        var prOpen = CodesOf(Issue(), Stage(IssueStage.PrOpen));
        var withRequest = CodesOf(Issue(labels: Labels.Implement), Stage(IssueStage.ReadySimple));

        Assert.DoesNotContain(ActionCodes.Ask, parent);
        Assert.DoesNotContain(ActionCodes.Ask, implementing);
        Assert.DoesNotContain(ActionCodes.Ask, prOpen);
        Assert.DoesNotContain(ActionCodes.Ask, withRequest);
    }

    [Fact]
    public void Only_answer_ask_and_fix_need_a_text()
    {
        var needText = new[]
            {
                ActionCodes.Implement, ActionCodes.Breakdown, ActionCodes.Retry, ActionCodes.Answer, ActionCodes.Ask,
                ActionCodes.StartNextSubIssue, ActionCodes.ReviewFull, ActionCodes.Fix,
            }
            .Where(code => IssueActions.Find(code)!.NeedsText)
            .ToList();

        Assert.Equal([ActionCodes.Answer, ActionCodes.Ask, ActionCodes.Fix], needText);
        Assert.Null(IssueActions.Find("set-model"));
    }

    // ---- PRs ----

    private static HostPull Pull(
        string title = "t",
        string state = "open",
        bool? draft = null,
        string author = "alice",
        params string[] labels) =>
        new(
            5,
            title,
            new HostRef("feature/x", "s"),
            new HostRef("main", "b"),
            Now,
            User: new HostUser(author),
            State: state,
            Draft: draft,
            Labels: labels.Select(label => new HostLabel(label)).ToList());

    private static IReadOnlyList<string> PullCodes(HostPull pull, ProjectSettings? settings = null, bool isBusy = false)
    {
        var input = new PullActionInput(pull, settings ?? Settings(), "bot", isBusy);

        return IssueActions.AvailableForPull(input)
            .Select(action => action.Code)
            .ToList();
    }

    [Fact]
    public void A_pull_with_the_trigger_label_offers_full_review_and_fix()
    {
        var codes = PullCodes(Pull(labels: "ai"));

        Assert.Equal([ActionCodes.ReviewFull, ActionCodes.Fix], codes);
    }

    [Fact]
    public void A_pull_the_runner_opened_is_in_scope_by_its_branch_and_author()
    {
        var ownPull = Pull(author: "bot") with { Head = new HostRef("ai/issue-7", "s") };

        var codes = PullCodes(ownPull);

        Assert.Equal([ActionCodes.ReviewFull, ActionCodes.Fix], codes);
    }

    [Fact]
    public void A_pull_out_of_scope_is_offered_nothing_when_review_is_scoped()
    {
        var codes = PullCodes(Pull());

        Assert.Empty(codes);
    }

    [Fact]
    public void With_review_scope_all_a_pull_out_of_scope_gets_a_full_review_but_no_fix()
    {
        var settings = Settings();
        settings.Review.Scope = ReviewConfig.AllValue;

        var codes = PullCodes(Pull(), settings);

        Assert.Equal([ActionCodes.ReviewFull], codes);
    }

    [Fact]
    public void Full_review_is_not_offered_again_while_the_label_is_there()
    {
        var codes = PullCodes(Pull(labels: ["ai", Labels.ReviewFull]));

        Assert.Equal([ActionCodes.Fix], codes);
    }

    [Theory]
    [InlineData("closed", null, "t")]
    [InlineData("open", true, "t")]
    [InlineData("open", null, "[skip-ai] wip")]
    public void A_closed_draft_or_skipped_pull_is_offered_nothing(string state, bool? draft, string title)
    {
        var codes = PullCodes(Pull(title, state, draft, labels: "ai"));

        Assert.Empty(codes);
    }

    [Fact]
    public void A_pull_with_a_job_is_offered_nothing()
    {
        var codes = PullCodes(Pull(labels: "ai"), isBusy: true);

        Assert.Empty(codes);
    }

    [Fact]
    public void A_workflow_that_is_off_hides_the_pull_actions()
    {
        var withoutReview = PullCodes(Pull(labels: "ai"), Settings(Workflows.Fix));
        var withoutFix = PullCodes(Pull(labels: "ai"), Settings(Workflows.Review));

        Assert.Equal([ActionCodes.Fix], withoutReview);
        Assert.Equal([ActionCodes.ReviewFull], withoutFix);
    }

    // ---- the writes ----

    private static ActionWrite? WriteOf(string code, string? text = null, int number = 7)
    {
        var context = new ActionWriteContext(new IssueRef("o/r", number), text, new ProjectSettings(), "laptop-1");

        return IssueActions.WriteOf(code, context);
    }

    [Theory]
    [InlineData(ActionCodes.Implement, ActionWriteKind.AddLabel, "ai-implement")]
    [InlineData(ActionCodes.StartNextSubIssue, ActionWriteKind.AddLabel, "ai-implement")]
    [InlineData(ActionCodes.Breakdown, ActionWriteKind.AddLabel, "ai-breakdown")]
    [InlineData(ActionCodes.ReviewFull, ActionWriteKind.AddLabel, "ai-review-full")]
    [InlineData(ActionCodes.Retry, ActionWriteKind.RemoveLabel, "ai:failed")]
    public void A_label_action_writes_its_label(string code, ActionWriteKind kind, string label)
    {
        var write = WriteOf(code);

        Assert.Equal(new ActionWrite(kind, new IssueRef("o/r", 7), label), write);
    }

    [Theory]
    [InlineData(ActionCodes.Answer)]
    [InlineData(ActionCodes.Ask)]
    [InlineData(ActionCodes.Fix)]
    public void A_text_action_writes_a_people_comment_with_the_keyword_and_the_trace_line(string code)
    {
        var write = WriteOf(code, "  Use the v2 endpoint.  ");

        Assert.Equal(ActionWriteKind.Comment, write!.Kind);
        Assert.Equal("@ai Use the v2 endpoint.\n\n_(sent from the ai-runner dashboard on laptop-1)_", write.Value);
        Assert.False(BotComments.IsBot(new HostComment(1, write.Value, new HostUser("me"), Now)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_text_action_without_a_text_has_no_write(string? text)
    {
        Assert.Null(WriteOf(ActionCodes.Answer, text));
    }

    [Fact]
    public void A_typed_marker_cannot_make_the_comment_a_bot_comment()
    {
        var write = WriteOf(ActionCodes.Ask, $"hello {BotComments.Marker}");

        Assert.False(BotComments.IsBot(new HostComment(1, write!.Value, new HostUser("me"), Now)));
    }

    [Fact]
    public void An_unknown_code_has_no_write()
    {
        Assert.Null(WriteOf("set-model", "x"));
    }
}
