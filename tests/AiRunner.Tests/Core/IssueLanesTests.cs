namespace AiRunner.Tests.Core;

public sealed class IssueLanesTests
{
    private static readonly CodeLabel FailedFlag = new(IssueMarks.Failed, "Failed");
    private static readonly CodeLabel RunningBadge = new(IssueMarks.Running, "Running");

    private static StageResult Stage(string code, CodeLabel? flag = null, CodeLabel? badge = null)
    {
        var label = IssueStage.Stages.First(stage => stage.Code == code);
        IReadOnlyList<CodeLabel> flags = flag is null ? [] : [flag];

        return new StageResult(label, flags, badge);
    }

    [Theory]
    [InlineData(IssueStage.New, IssueLane.ToDo)]
    [InlineData(IssueStage.QuestionsOpen, IssueLane.ToDo)]
    [InlineData(IssueStage.ReadySimple, IssueLane.Ready)]
    [InlineData(IssueStage.ReadyComplex, IssueLane.Ready)]
    [InlineData(IssueStage.Implementing, IssueLane.InProgress)]
    [InlineData(IssueStage.BrokenDown, IssueLane.InProgress)]
    [InlineData(IssueStage.PrOpen, IssueLane.InReview)]
    [InlineData(IssueStage.Merged, IssueLane.Finished)]
    [InlineData(IssueStage.Done, IssueLane.Finished)]
    [InlineData(IssueStage.ClosedNoMerge, IssueLane.Finished)]
    [InlineData("some-new-stage", IssueLane.ToDo)]
    public void Every_stage_has_one_lane(string stage, string lane)
    {
        Assert.Equal(lane, IssueLane.Of(stage).Code);
    }

    [Fact]
    public void Lanes_go_left_to_right_and_every_stage_lands_in_one_of_them()
    {
        Assert.Equal(
            ["todo", "ready", "in-progress", "in-review", "finished"],
            IssueLane.Lanes.Select(lane => lane.Code));
        Assert.All(IssueStage.Stages, stage => Assert.Contains(IssueLane.Of(stage.Code), IssueLane.Lanes));
    }

    [Fact]
    public void Needs_you_follows_the_waiting_on_action()
    {
        var waiting = new CodeLabel(WaitingOn.AnswerQuestions, "Answer the questions with @ai");
        var stage = Stage(IssueStage.QuestionsOpen);

        Assert.True(IssueFocus.Matches(IssueFocus.NeedsYou, stage, waiting));
        Assert.False(IssueFocus.Matches(IssueFocus.NeedsYou, stage, null));
    }

    [Fact]
    public void Working_means_a_job_is_queued_or_running_and_failed_means_the_flag()
    {
        var running = Stage(IssueStage.Implementing, badge: RunningBadge);
        var failed = Stage(IssueStage.Implementing, flag: FailedFlag);

        Assert.True(IssueFocus.Matches(IssueFocus.Working, running, null));
        Assert.False(IssueFocus.Matches(IssueFocus.Working, failed, null));
        Assert.True(IssueFocus.Matches(IssueFocus.Failed, failed, null));
        Assert.False(IssueFocus.Matches(IssueFocus.Failed, running, null));
        Assert.False(IssueFocus.Matches("nope", failed, null));
    }
}
