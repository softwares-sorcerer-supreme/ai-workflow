using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Jobs;

public sealed class AnalyzeIssueJobTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private AnalyzeIssueJob Job => new(_kit.Config, _kit.GitBucket, _kit.Workspace, _kit.Copilot, _kit.State);

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Ready_simple_posts_analysis_and_spec_and_sets_labels()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", "Please add CSV export", "ai", Labels.QuestionsOpen);
        _kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("ready", "simple"), Samples.Spec);

        var outcome = await Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        var comments = _kit.GitBucket.CommentsOf(12);
        Assert.Equal(2, comments.Count);
        Assert.True(BotComments.IsAnalysis(comments[0]));
        Assert.True(BotComments.Is(comments[1], Markers.Spec));
        Assert.DoesNotContain("Thinking", comments[0].Body);
        Assert.Equal(new[] { "ai", Labels.Ready, Labels.Simple }.Order(), _kit.GitBucket.LabelsOf(12).Order());

        var request = Assert.Single(_kit.Copilot.Requests);
        Assert.Equal("grill-issue", request.Agent);
        Assert.Equal(["write", "shell"], request.DenyTools);
        Assert.Contains("# Issue #12: Export CSV", File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "context.md")));
    }

    [Fact]
    public async Task Questions_open_posts_one_comment()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", "Please add CSV export", "ai");
        _kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("questions-open", "simple"));

        await Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default);

        Assert.Single(_kit.GitBucket.CommentsOf(12));
        Assert.Contains(Labels.QuestionsOpen, _kit.GitBucket.LabelsOf(12));
    }

    [Fact]
    public async Task Missing_markers_fail_without_posting()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", null, "ai");
        _kit.Copilot.StdOut = "I could not do it.";

        await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default));
        Assert.Empty(_kit.GitBucket.CommentsOf(12));
    }

    [Fact]
    public async Task Ready_without_spec_fails_without_posting()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", null, "ai");
        _kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("ready", "simple"));

        await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default));
        Assert.Empty(_kit.GitBucket.CommentsOf(12));
    }

    [Fact]
    public async Task Quota_error_gets_a_clear_message()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", null, "ai");
        _kit.Copilot.ExitCode = 1;
        _kit.Copilot.StdErr = "Error: You have exceeded your premium request allowance";

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default));
        Assert.Contains("premium requests", ex.Message);
    }

    [Fact]
    public async Task Duplicate_run_does_nothing()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", null, "ai");
        _kit.GitBucket.AddBotComment(12, Markers.Wrap("old", Markers.Analysis));

        var outcome = await Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default);

        Assert.True(outcome.Quiet);
        Assert.Empty(_kit.Copilot.Requests);
    }

    [Fact]
    public async Task Second_round_remembers_the_handled_comment()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", null, "ai", Labels.QuestionsOpen);
        _kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.Analysis("questions-open", "simple"), Markers.Analysis));
        var answer = _kit.GitBucket.AddHumanComment(12, "Q1: all columns @ai");
        _kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("questions-open", "complex"));

        await Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default);

        Assert.Equal(answer.Id, _kit.State.GetLastHandledComment(JobTestKit.Repo, 12));
        Assert.Contains("Analysis round: 2", File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "context.md")));
    }

    [Fact]
    public async Task Round_limit_stops_with_a_message()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", null, "ai");
        for (var i = 0; i < 5; i++) _kit.GitBucket.AddBotComment(12, Markers.Wrap("a", Markers.Analysis));
        _kit.GitBucket.AddHumanComment(12, "still unclear @ai");

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default));
        Assert.Contains("call", ex.Message);
        Assert.Empty(_kit.Copilot.Requests);
    }
}

public sealed class BreakdownIssueJobTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private BreakdownIssueJob Job => new(_kit.Config, _kit.GitBucket, _kit.Workspace, _kit.Git, _kit.State);

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Creates_feature_branch_sub_issues_and_parent_list()
    {
        _kit.GitBucket.AddIssue(12, "Export", null, "ai", Labels.Ready, Labels.Complex, Labels.Breakdown);
        _kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.Analysis("ready", "complex", Samples.BreakdownTable), Markers.Analysis));

        var outcome = await Job.RunAsync(_kit.Request(JobType.BreakdownIssue, 12), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Contains(("origin/main", "feat/issue-12"), _kit.Git.Pushes);

        var subs = _kit.GitBucket.Issues.Values.Where(i => i.HasLabel(Labels.SubIssue)).OrderBy(i => i.Number).ToList();
        Assert.Equal(["#12.1 Export entity", "#12.2 Export command", "#12.3 Export API"], subs.Select(s => s.Title));
        Assert.All(subs, s => Assert.True(s.HasLabel("ai")));
        Assert.True(subs[0].HasLabel(Labels.Simple));
        Assert.False(subs[2].HasLabel(Labels.Simple));           // [developer] row
        Assert.False(subs[0].HasLabel(Labels.Implement));        // no auto-start by default
        Assert.Equal(12, SubIssueBody.Parse(subs[1].Body)!.Parent);

        Assert.Equal(subs.Select(s => s.Number), SubIssueBody.FindList(_kit.GitBucket.CommentsOf(12)));
        Assert.Contains(Labels.Parent, _kit.GitBucket.LabelsOf(12));
        Assert.DoesNotContain(Labels.Breakdown, _kit.GitBucket.LabelsOf(12));
    }

    [Fact]
    public async Task Auto_start_labels_the_first_sub_issue()
    {
        using var kit = new JobTestKit(r => r.AutoStartFirstSubIssue = true);
        kit.GitBucket.AddIssue(12, "Export", null, "ai", Labels.Ready, Labels.Complex, Labels.Breakdown);
        kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.Analysis("ready", "complex", Samples.BreakdownTable), Markers.Analysis));

        await new BreakdownIssueJob(kit.Config, kit.GitBucket, kit.Workspace, kit.Git, kit.State)
            .RunAsync(kit.Request(JobType.BreakdownIssue, 12), kit.Run, default);

        var first = kit.GitBucket.Issues.Values.Single(i => i.Title.StartsWith("#12.1 "));
        Assert.True(first.HasLabel(Labels.Implement));
    }

    [Fact]
    public async Task Without_ready_and_complex_it_explains_and_removes_the_label()
    {
        _kit.GitBucket.AddIssue(12, "Export", null, "ai", Labels.Breakdown, Labels.QuestionsOpen);

        var outcome = await Job.RunAsync(_kit.Request(JobType.BreakdownIssue, 12), _kit.Run, default);

        Assert.Equal("rejected", outcome.Result);
        Assert.DoesNotContain(Labels.Breakdown, _kit.GitBucket.LabelsOf(12));
        Assert.Contains("did not create", _kit.GitBucket.CommentsOf(12).Single().Body);
        Assert.Empty(_kit.Git.Pushes);
    }
}
