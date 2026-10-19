using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

public sealed class WaitingOnTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly CodeLabel FailedFlag = new(IssueMarks.Failed, "Failed");
    private static readonly CodeLabel QueuedBadge = new(IssueMarks.Queued, "Queued");
    private static readonly CodeLabel RunningBadge = new(IssueMarks.Running, "Running");

    private static GbPull Pull(int number, bool isOpen = true) =>
        new(number, "t", new GbRef("ai/issue-7", "s"), new GbRef("main", "b"), Now, State: isOpen ? "open" : "closed");

    private static IssueSnapshot Issue(bool isOpen = true, params string[] labels) =>
        SnapshotRules.IssueRow(TestProjects.SeparateIssuesRepo(), Samples.Issue(7, labels) with
        {
            State = isOpen ? "open" : "closed"
        }, Now);

    private static StageResult Stage(
        string code,
        CodeLabel? flag = null,
        CodeLabel? badge = null,
        params GbPull[] pulls)
    {
        var label = IssueStage.Stages.First(stage => stage.Code == code);
        IReadOnlyList<CodeLabel> flags = flag is null ? [] : [flag];

        return new StageResult(label, flags, badge)
        {
            Pulls = pulls,
        };
    }

    private static CodeLabel? Waiting(
        IssueSnapshot issue,
        StageResult stage,
        bool autoStart = false,
        params SubIssueProgress[] subIssues) =>
        WaitingOn.Of(new WaitingInput(issue, stage, autoStart, subIssues));

    // ---- one case per action of the spec ----

    [Fact]
    public void Questions_open_waits_on_an_answer()
    {
        var action = Waiting(Issue(), Stage(IssueStage.QuestionsOpen));

        Assert.Equal((WaitingOn.AnswerQuestions, "Answer the questions with @ai"), (action?.Code, action?.Label));
    }

    [Fact]
    public void Ready_simple_waits_on_ai_implement_until_it_is_added()
    {
        var without = Waiting(Issue(), Stage(IssueStage.ReadySimple));
        var with = Waiting(Issue(labels: Labels.Implement), Stage(IssueStage.ReadySimple));

        Assert.Equal((WaitingOn.AddImplement, "Add ai-implement"), (without?.Code, without?.Label));
        Assert.Null(with);
    }

    [Fact]
    public void Ready_complex_waits_on_ai_breakdown_until_it_is_added()
    {
        var without = Waiting(Issue(), Stage(IssueStage.ReadyComplex));
        var with = Waiting(Issue(labels: Labels.Breakdown), Stage(IssueStage.ReadyComplex));

        Assert.Equal((WaitingOn.AddBreakdown, "Add ai-breakdown"), (without?.Code, without?.Label));
        Assert.Null(with);
    }

    [Fact]
    public void Pr_open_names_the_open_pr()
    {
        var stage = Stage(IssueStage.PrOpen, pulls: [Pull(40, isOpen: false), Pull(41)]);

        var action = Waiting(Issue(), stage);

        Assert.Equal((WaitingOn.ReviewAndMerge, "Review and merge PR #41"), (action?.Code, action?.Label));
    }

    [Fact]
    public void Failed_waits_on_a_human_and_beats_the_stage()
    {
        var action = Waiting(Issue(), Stage(IssueStage.QuestionsOpen, FailedFlag));

        Assert.Equal((WaitingOn.ReadFailure, "Read the failure, then remove ai:failed"), (action?.Code, action?.Label));
    }

    // ---- nobody ----

    [Theory]
    [InlineData(IssueStage.New)]
    [InlineData(IssueStage.Implementing)]
    [InlineData(IssueStage.Merged)]
    public void Stages_the_runner_moves_by_itself_wait_on_nobody(string stage) =>
        Assert.Null(Waiting(Issue(), Stage(stage)));

    [Theory]
    [InlineData(IssueMarks.Queued)]
    [InlineData(IssueMarks.Running)]
    public void A_queued_or_running_issue_waits_on_nobody(string badge)
    {
        var mark = badge == IssueMarks.Queued ? QueuedBadge : RunningBadge;

        Assert.Null(Waiting(Issue(), Stage(IssueStage.QuestionsOpen, FailedFlag, mark)));
    }

    [Fact]
    public void A_closed_issue_waits_on_nobody()
    {
        var closed = Issue(isOpen: false);

        Assert.Null(Waiting(closed, Stage(IssueStage.Done, FailedFlag)));
    }

    // ---- start the next sub-issue ----

    private static readonly SubIssueProgress Waiting1 = new(true, IssueStage.New, false);

    [Fact]
    public void Broken_down_parent_waits_on_a_start_when_nothing_runs_and_auto_start_is_off()
    {
        var action = Waiting(Issue(), Stage(IssueStage.BrokenDown), false, Waiting1);

        Assert.Equal((WaitingOn.StartNextSubIssue, "Start the next sub-issue"), (action?.Code, action?.Label));
    }

    [Fact]
    public void Auto_start_on_means_the_runner_starts_it_and_nobody_waits()
    {
        Assert.Null(Waiting(Issue(), Stage(IssueStage.BrokenDown), true, Waiting1));
    }

    [Theory]
    [InlineData(IssueStage.Implementing, true, false)]
    [InlineData(IssueStage.PrOpen, true, false)]
    [InlineData(IssueStage.New, true, true)]
    public void A_sub_issue_in_progress_or_with_a_job_means_no_start_is_needed(string code, bool isOpen, bool isBusy)
    {
        var inProgress = new SubIssueProgress(isOpen, code, isBusy);

        Assert.Null(Waiting(Issue(), Stage(IssueStage.BrokenDown), false, inProgress, Waiting1));
    }

    [Fact]
    public void A_closed_sub_issue_in_a_pr_stage_does_not_count_as_in_progress()
    {
        var done = new SubIssueProgress(false, IssueStage.PrOpen, false);

        var action = Waiting(Issue(), Stage(IssueStage.BrokenDown), false, done, Waiting1);

        Assert.Equal(WaitingOn.StartNextSubIssue, action?.Code);
    }

    [Fact]
    public void With_every_sub_issue_closed_there_is_nothing_to_start()
    {
        var done = new SubIssueProgress(false, IssueStage.Done, false);

        Assert.Null(Waiting(Issue(), Stage(IssueStage.BrokenDown), false, done));
    }
}
