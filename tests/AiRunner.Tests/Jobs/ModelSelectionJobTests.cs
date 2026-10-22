using AiRunner.Configuration;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Jobs;

/// <summary>The model each Copilot run gets (<see cref="ModelResolver"/> through the jobs) and what the job records.</summary>
public sealed class ModelSelectionJobTests : IDisposable
{
    private readonly JobTestKit _kit;

    public ModelSelectionJobTests()
    {
        _kit = new JobTestKit(settings =>
        {
            settings.Models[Workflows.Implement] = new ModelSetting
            {
                Default = "m-default",
                Simple = "m-simple",
                Complex = "m-complex",
                FixRounds = "m-fix",
            };
            settings.Models[Workflows.Analyze] = new ModelSetting { Default = "m-analyze" };
        });
    }

    private ImplementIssueJob Implement =>
        new(_kit.Config, _kit.CodeHost, _kit.Loader, _kit.Workspace, _kit.Git, _kit.Session, _kit.Attachments);

    private AnalyzeIssueJob Analyze =>
        new(_kit.Config, _kit.CodeHost, _kit.Loader, _kit.Workspace, _kit.Copilot, _kit.State, _kit.Attachments);

    public void Dispose() => _kit.Dispose();

    private void IssueWithSpec(params string[] extraLabels)
    {
        string[] labels = ["ai", Labels.Ready, Labels.Implement, .. extraLabels];
        _kit.CodeHost.AddIssue(12, "Export CSV", "body", labels);
        _kit.CodeHost.AddBotComment(12, Markers.Wrap(Samples.Spec, Markers.Spec));
    }

    [Fact]
    public async Task Round_one_uses_the_issue_model_and_a_fix_round_uses_fix_rounds()
    {
        IssueWithSpec(Labels.Simple);
        _kit.Processes.ExitCodeSequence["make test"] = new Queue<int>([1, 0]);

        var request = _kit.Request(JobType.ImplementIssue, 12);
        await Implement.RunAsync(request, _kit.Run, default);

        Assert.Equal(["m-simple", "m-fix"], _kit.Copilot.Requests.Select(copilot => copilot.Model));
        Assert.Equal("m-simple", _kit.Run.Model);
        Assert.Equal(ModelResolver.ReasonSimple, _kit.Run.ModelReason);
        var pull = _kit.CodeHost.Pulls.Single();
        Assert.Contains("Round 2 (fix): model m-fix (fix rounds);", pull.Body);
        Assert.DoesNotContain("Round 1 (implement): model", pull.Body);
    }

    [Fact]
    public async Task A_simple_issue_uses_the_simple_model()
    {
        IssueWithSpec(Labels.Simple);

        var request = _kit.Request(JobType.ImplementIssue, 12);
        await Implement.RunAsync(request, _kit.Run, default);

        Assert.Equal("m-simple", _kit.Copilot.Requests.Single().Model);
        Assert.Equal(ModelResolver.ReasonSimple, _kit.Run.ModelReason);
    }

    [Fact]
    public async Task A_bot_override_beats_every_setting_in_every_round()
    {
        IssueWithSpec(Labels.Simple);
        _kit.CodeHost.AddBotComment(12, Markers.SettingTag("models.implement", "m-override"));
        _kit.Processes.ExitCodeSequence["make test"] = new Queue<int>([1, 0]);

        var request = _kit.Request(JobType.ImplementIssue, 12);
        await Implement.RunAsync(request, _kit.Run, default);

        Assert.Equal(["m-override", "m-override"], _kit.Copilot.Requests.Select(copilot => copilot.Model));
        Assert.Equal(ModelResolver.ReasonOverride, _kit.Run.ModelReason);
        Assert.DoesNotContain("model m-override", _kit.CodeHost.Pulls.Single().Body);
    }

    [Fact]
    public async Task A_person_typing_the_tag_changes_nothing()
    {
        IssueWithSpec(Labels.Simple);
        _kit.CodeHost.AddHumanComment(12, Markers.SettingTag("models.implement", "m-human"));

        var request = _kit.Request(JobType.ImplementIssue, 12);
        await Implement.RunAsync(request, _kit.Run, default);

        Assert.Equal("m-simple", _kit.Copilot.Requests.Single().Model);
    }

    [Fact]
    public async Task A_workflow_without_a_setting_runs_with_the_copilot_default_and_says_so()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", "Please add CSV export", "ai");
        _kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("ready", "simple"), Samples.Spec);
        _kit.Settings.Models.Clear();

        var request = _kit.Request(JobType.AnalyzeIssue, 12);
        await Analyze.RunAsync(request, _kit.Run, default);

        Assert.Null(_kit.Copilot.Requests.Single().Model);
        Assert.Null(_kit.Run.Model);
        Assert.Equal(ModelResolver.ReasonNone, _kit.Run.ModelReason);
    }

    [Fact]
    public async Task Analysis_uses_its_model_and_records_it()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", "Please add CSV export", "ai");
        _kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("ready", "simple"), Samples.Spec);

        var request = _kit.Request(JobType.AnalyzeIssue, 12);
        await Analyze.RunAsync(request, _kit.Run, default);

        Assert.Equal("m-analyze", _kit.Copilot.Requests.Single().Model);
        Assert.Equal(("m-analyze", ModelResolver.ReasonDefault), (_kit.Run.Model, _kit.Run.ModelReason));
    }

    [Fact]
    public async Task A_sub_issue_reads_its_parents_override_and_complexity_label()
    {
        _kit.CodeHost.AddIssue(10, "Parent", "body", "ai", Labels.Parent, Labels.Complex);
        _kit.CodeHost.AddBotComment(10, Markers.SettingTag("models.implement", "m-parent"));
        var subBody = "Parent: #10\nOrder: 1 of 2\nBase branch: feat/issue-10\nComponent: demo\n\nScope";
        _kit.CodeHost.AddIssue(11, "#10.1 Part", subBody, "ai", Labels.SubIssue);

        var withOverride = await _kit.CodeHost.ReadAsync(
            new IssueRef("james/demo", 11), Workflows.Implement, _kit.Run, default);

        Assert.Equal("m-parent", withOverride.IssueOverride);
        Assert.Equal(ModelComplexity.Complex, withOverride.Complexity);

        _kit.CodeHost.AddBotComment(11, Markers.SettingTag("models.implement", "m-own"));

        var withOwn = await _kit.CodeHost.ReadAsync(
            new IssueRef("james/demo", 11), Workflows.Implement, _kit.Run, default);

        Assert.Equal("m-own", withOwn.IssueOverride);
    }
}
