using AiRunner.Infrastructure.CodeHost;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Jobs;

public sealed class AnalyzeIssueJobTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private AnalyzeIssueJob Job =>
        new(_kit.Config, _kit.CodeHost, _kit.Loader, _kit.Workspace, _kit.Copilot, _kit.State, _kit.Attachments);

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Ready_simple_posts_analysis_and_spec_and_sets_labels()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", "Please add CSV export", "ai", Labels.QuestionsOpen);
        var analysis = Samples.Analysis("ready", "simple");
        _kit.Copilot.StdOut = Samples.AgentOutput(analysis, Samples.Spec);

        var jobRequest = _kit.Request(JobType.AnalyzeIssue, 12);
        var outcome = await Job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(1, outcome.CopilotCalls);

        var comments = _kit.CodeHost.CommentsOf(12);
        Assert.Equal(2, comments.Count);
        Assert.True(BotComments.IsAnalysis(comments[0]));
        Assert.True(BotComments.Is(comments[1], Markers.Spec));
        Assert.DoesNotContain("Thinking", comments[0].Body);
        Assert.Equal(new[] { "ai", Labels.Ready, Labels.Simple }.Order(), _kit.CodeHost.LabelsOf(12).Order());

        var request = Assert.Single(_kit.Copilot.Requests);
        Assert.Equal("grill-issue", request.Agent);
        Assert.Equal(Path.Combine(_kit.Run.Dir, "copilot-usage.json"), request.UsageFile);
        Assert.Contains("AGENTS.md in this folder", request.Prompt);
        Assert.Equal(["write", "shell"], request.DenyTools);
        var contextFile = Path.Combine(_kit.Workspace.Dir, "context.md");
        Assert.Contains("# Issue #12: Export CSV", File.ReadAllText(contextFile));
    }

    [Fact]
    public async Task Questions_open_posts_one_comment()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", "Please add CSV export", "ai");
        var analysis = Samples.Analysis("questions-open", "simple");
        _kit.Copilot.StdOut = Samples.AgentOutput(analysis);

        var jobRequest = _kit.Request(JobType.AnalyzeIssue, 12);
        await Job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Single(_kit.CodeHost.CommentsOf(12));
        Assert.Contains(Labels.QuestionsOpen, _kit.CodeHost.LabelsOf(12));
    }

    [Fact]
    public async Task Missing_markers_fail_without_posting()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", null, "ai");
        _kit.Copilot.StdOut = "I could not do it.";

        await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default));

        Assert.Empty(_kit.CodeHost.CommentsOf(12));
    }

    [Fact]
    public async Task Ready_without_spec_fails_without_posting()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", null, "ai");
        var analysis = Samples.Analysis("ready", "simple");
        _kit.Copilot.StdOut = Samples.AgentOutput(analysis);

        await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default));

        Assert.Empty(_kit.CodeHost.CommentsOf(12));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("| 1 | Only five | cells | here | - |")]
    public async Task Ready_complex_with_an_unreadable_breakdown_fails_without_posting(string breakdown)
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", null, "ai");
        var analysis = Samples.Analysis("ready", "complex", breakdown);
        _kit.Copilot.StdOut = Samples.AgentOutput(analysis, Samples.Spec);

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default));

        Assert.Contains("proposed breakdown", exception.Message);
        Assert.Empty(_kit.CodeHost.CommentsOf(12));
    }

    [Fact]
    public async Task Quota_error_gets_a_clear_message()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", null, "ai");
        _kit.Copilot.ExitCode = 1;
        _kit.Copilot.StdErr = "Error: You have exceeded your premium request allowance";

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default));

        Assert.Contains("premium requests", exception.Message);
    }

    [Fact]
    public async Task Duplicate_run_does_nothing()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", null, "ai");
        var analysisComment = Markers.Wrap("old", Markers.Analysis);
        _kit.CodeHost.AddBotComment(12, analysisComment);

        var jobRequest = _kit.Request(JobType.AnalyzeIssue, 12);
        var outcome = await Job.RunAsync(jobRequest, _kit.Run, default);

        Assert.True(outcome.Quiet);
        Assert.Empty(_kit.Copilot.Requests);
    }

    [Fact]
    public async Task Second_round_remembers_the_handled_comment()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", null, "ai", Labels.QuestionsOpen);
        var firstAnalysis = Samples.Analysis("questions-open", "simple");
        var analysisComment = Markers.Wrap(firstAnalysis, Markers.Analysis);
        _kit.CodeHost.AddBotComment(12, analysisComment);
        var answer = _kit.CodeHost.AddHumanComment(12, "Q1: all columns @ai");
        var secondAnalysis = Samples.Analysis("questions-open", "complex");
        _kit.Copilot.StdOut = Samples.AgentOutput(secondAnalysis);

        var jobRequest = _kit.Request(JobType.AnalyzeIssue, 12);
        await Job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Equal(answer.Id, _kit.State.GetLastHandledComment(JobTestKit.Repo, 12));
        var contextFile = Path.Combine(_kit.Workspace.Dir, "context.md");
        Assert.Contains("Analysis round: 2", File.ReadAllText(contextFile));
    }

    [Fact]
    public async Task Round_limit_stops_with_a_message()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", null, "ai");
        for (var index = 0; index < 5; index++)
        {
            var analysisComment = Markers.Wrap("a", Markers.Analysis);
            _kit.CodeHost.AddBotComment(12, analysisComment);
        }

        _kit.CodeHost.AddHumanComment(12, "still unclear @ai");

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.AnalyzeIssue, 12), _kit.Run, default));

        Assert.Contains("call", exception.Message);
        Assert.Empty(_kit.Copilot.Requests);
    }
}

public sealed class BreakdownIssueJobTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private BreakdownIssueJob Job =>
        new(_kit.CodeHost, _kit.Loader, _kit.Workspace, _kit.Git);

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Creates_feature_branch_sub_issues_and_parent_list()
    {
        _kit.CodeHost.AddIssue(12, "Export", null, "ai", Labels.Ready, Labels.Complex, Labels.Breakdown);
        var analysis = Samples.Analysis("ready", "complex", Samples.BreakdownTable);
        var analysisComment = Markers.Wrap(analysis, Markers.Analysis);
        _kit.CodeHost.AddBotComment(12, analysisComment);

        var jobRequest = _kit.Request(JobType.BreakdownIssue, 12);
        var outcome = await Job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(0, outcome.CopilotCalls);   // the breakdown reads the analysis; it never starts Copilot
        Assert.Contains(("origin/main", "feat/issue-12"), _kit.Git.Pushes);

        var subIssues = _kit.CodeHost.Issues.Values
            .Where(issue => issue.HasLabel(Labels.SubIssue))
            .OrderBy(issue => issue.Number)
            .ToList();
        Assert.Equal(
            ["#12.1 Export entity", "#12.2 Export command", "#12.3 Export API"],
            subIssues.Select(issue => issue.Title));
        Assert.All(subIssues, issue => Assert.True(issue.HasLabel("ai")));
        Assert.True(subIssues[0].HasLabel(Labels.Simple));
        Assert.False(subIssues[2].HasLabel(Labels.Simple));           // [developer] row
        Assert.False(subIssues[0].HasLabel(Labels.Implement));        // no auto-start by default
        Assert.Equal(12, SubIssueBody.Parse(subIssues[1].Body)!.Parent);
        Assert.Equal(Samples.DefaultComponent, SubIssueBody.Parse(subIssues[1].Body)!.Component);

        var parentComments = _kit.CodeHost.CommentsOf(12);
        Assert.Equal(
            subIssues.Select(issue => issue.Number),
            SubIssueBody.FindList(parentComments));
        Assert.Contains(Labels.Parent, _kit.CodeHost.LabelsOf(12));
        Assert.DoesNotContain(Labels.Breakdown, _kit.CodeHost.LabelsOf(12));
    }

    [Fact]
    public async Task Sub_issues_get_their_labels_on_a_host_that_drops_them_on_create()
    {
        _kit.CodeHost.DropsLabelsOnCreate = true;

        var subIssues = await BreakDownAsync();

        Assert.All(subIssues, issue => Assert.True(issue.HasLabel(Labels.SubIssue)));
        Assert.All(subIssues, issue => Assert.True(issue.HasLabel("ai")));
    }

    [Fact]
    public async Task A_host_that_keeps_labels_on_create_is_not_asked_again_for_the_sub_issues()
    {
        _kit.CodeHost.Capabilities = new CodeHostCapabilities(KeepsLabelsOnCreate: true);

        var subIssues = await BreakDownAsync();
        var subIssueNumbers = subIssues.Select(issue => issue.Number).ToHashSet();

        Assert.Equal(3, subIssues.Count);
        Assert.DoesNotContain(_kit.CodeHost.IssueReads, subIssueNumbers.Contains);
    }

    [Fact]
    public async Task A_sub_issue_closed_before_a_retry_is_reused_and_not_created_again()
    {
        _kit.CodeHost.AddIssue(12, "Export", null, "ai", Labels.Ready, Labels.Complex, Labels.Breakdown);
        var analysis = Samples.Analysis("ready", "complex", Samples.BreakdownTable);
        _kit.CodeHost.AddBotComment(12, Markers.Wrap(analysis, Markers.Analysis));
        var firstTask = new SubTask(1, Samples.DefaultComponent, "Export entity", "Scope.", "src", [], false);
        var firstBody = SubIssueBody.Build(new SubIssueDraft(12, firstTask, 3, "contract"));
        _kit.CodeHost.AddIssue(20, "#12.1 Export entity", firstBody, "ai", Labels.SubIssue);
        _kit.CodeHost.Issues[20] = _kit.CodeHost.Issues[20] with { State = "closed" };

        await Job.RunAsync(_kit.Request(JobType.BreakdownIssue, 12), _kit.Run, default);

        var titles = _kit.CodeHost.Issues.Values
            .Where(issue => issue.HasLabel(Labels.SubIssue))
            .Select(issue => issue.Title)
            .ToList();
        Assert.Equal(3, titles.Count);
        Assert.Single(titles, title => title.StartsWith("#12.1 "));
        Assert.Equal(20, SubIssueBody.FindList(_kit.CodeHost.CommentsOf(12))![0]);
    }

    [Fact]
    public async Task Auto_start_labels_the_first_sub_issue()
    {
        using var kit = new JobTestKit(settings => settings.AutoStartFirstSubIssue = true);
        kit.CodeHost.AddIssue(12, "Export", null, "ai", Labels.Ready, Labels.Complex, Labels.Breakdown);
        var analysis = Samples.Analysis("ready", "complex", Samples.BreakdownTable);
        var analysisComment = Markers.Wrap(analysis, Markers.Analysis);
        kit.CodeHost.AddBotComment(12, analysisComment);

        var jobRequest = kit.Request(JobType.BreakdownIssue, 12);
        await new BreakdownIssueJob(kit.CodeHost, kit.Loader, kit.Workspace, kit.Git)
            .RunAsync(jobRequest, kit.Run, default);

        var first = kit.CodeHost.Issues.Values.Single(issue => issue.Title.StartsWith("#12.1 "));
        Assert.True(first.HasLabel(Labels.Implement));
    }

    [Fact]
    public async Task An_unreadable_breakdown_is_explained_and_the_label_removed_so_a_new_analysis_can_run()
    {
        _kit.CodeHost.AddIssue(12, "Export", null, "ai", Labels.Ready, Labels.Complex, Labels.Breakdown);
        var analysis = Samples.Analysis("ready", "complex", "None");
        _kit.CodeHost.AddBotComment(12, Markers.Wrap(analysis, Markers.Analysis));

        var outcome = await Job.RunAsync(_kit.Request(JobType.BreakdownIssue, 12), _kit.Run, default);

        Assert.Equal("rejected", outcome.Result);
        Assert.DoesNotContain(Labels.Breakdown, _kit.CodeHost.LabelsOf(12));
        Assert.DoesNotContain(Labels.Failed, _kit.CodeHost.LabelsOf(12));
        Assert.Contains("wrong format", _kit.CodeHost.CommentsOf(12).Last().Body);
        Assert.Contains("new analysis", _kit.CodeHost.CommentsOf(12).Last().Body);
        Assert.Empty(_kit.Git.Pushes);
        Assert.DoesNotContain(_kit.CodeHost.Issues.Values, issue => issue.HasLabel(Labels.SubIssue));
    }

    [Fact]
    public async Task The_parent_comment_does_not_promise_an_automatic_start_for_a_developer_row()
    {
        using var kit = new JobTestKit(settings => settings.AutoStartFirstSubIssue = true);
        kit.CodeHost.AddIssue(12, "Export", null, "ai", Labels.Ready, Labels.Complex, Labels.Breakdown);
        var breakdown = Samples.Breakdown(Samples.DefaultComponent, Samples.DefaultComponent, Samples.DefaultComponent)
            .Replace("Export entity", "[developer] Export entity");
        var analysis = Samples.Analysis("ready", "complex", breakdown);
        kit.CodeHost.AddBotComment(12, Markers.Wrap(analysis, Markers.Analysis));

        await new BreakdownIssueJob(kit.CodeHost, kit.Loader, kit.Workspace, kit.Git)
            .RunAsync(kit.Request(JobType.BreakdownIssue, 12), kit.Run, default);

        var parentComment = kit.CodeHost.CommentsOf(12).Last().Body!;
        Assert.DoesNotContain("starts automatically", parentComment);
        Assert.Contains("built by a developer", parentComment);
    }

    [Fact]
    public async Task Without_ready_and_complex_it_explains_and_removes_the_label()
    {
        _kit.CodeHost.AddIssue(12, "Export", null, "ai", Labels.Breakdown, Labels.QuestionsOpen);

        var jobRequest = _kit.Request(JobType.BreakdownIssue, 12);
        var outcome = await Job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Equal("rejected", outcome.Result);
        Assert.DoesNotContain(Labels.Breakdown, _kit.CodeHost.LabelsOf(12));
        Assert.Contains("did not create", _kit.CodeHost.CommentsOf(12).Single().Body);
        Assert.Empty(_kit.Git.Pushes);
    }

    private async Task<List<HostIssue>> BreakDownAsync()
    {
        _kit.CodeHost.AddIssue(12, "Export", null, "ai", Labels.Ready, Labels.Complex, Labels.Breakdown);
        var analysis = Samples.Analysis("ready", "complex", Samples.BreakdownTable);
        _kit.CodeHost.AddBotComment(12, Markers.Wrap(analysis, Markers.Analysis));

        await Job.RunAsync(_kit.Request(JobType.BreakdownIssue, 12), _kit.Run, default);

        return _kit.CodeHost.Issues.Values
            .Where(issue => issue.Title.StartsWith("#12.", StringComparison.Ordinal))
            .ToList();
    }
}
