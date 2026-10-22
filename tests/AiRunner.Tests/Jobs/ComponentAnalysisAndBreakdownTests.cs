using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Jobs;

/// <summary>
/// The analysis names its components and the breakdown puts one sub-issue per component in the issues repo.
/// Same assertions for the three layouts; each has two code components.
/// </summary>
public sealed class ComponentAnalysisAndBreakdownTests
{
    public static TheoryData<string> LayoutNames => ["multi-repo", "separate-issues-repo", "monorepo"];

    private static ProjectConfig Layout(string name) =>
        name switch
        {
            "multi-repo" => TestProjects.MultiRepo(),
            "separate-issues-repo" => TestProjects.SeparateIssuesRepo(),
            _ => TestProjects.Monorepo(),
        };

    private static AnalyzeIssueJob AnalyzeJob(JobTestKit kit) =>
        new(kit.Config, kit.CodeHost, kit.Loader, kit.Workspace, kit.Copilot, kit.State, kit.Attachments);

    private static BreakdownIssueJob BreakdownJob(JobTestKit kit) =>
        new(kit.CodeHost, kit.Loader, kit.Workspace, kit.Git);

    private static string[] CodeNames(ProjectConfig project) =>
        project.CodeComponents.Select(component => component.Name).ToArray();

    private static string CloneOf(JobTestKit kit, string componentName)
    {
        var component = kit.Project.Components.First(candidate => candidate.Name == componentName);

        return Path.Combine(kit.Workspace.Dir, ProjectConfig.FolderOf(component.Repo));
    }

    private static void PrepareParent(JobTestKit kit, string table)
    {
        kit.CodeHost.AddIssue(12, "Export", null, "ai", Labels.Ready, Labels.Complex, Labels.Breakdown);
        var analysis = Samples.Analysis("ready", "complex", table, components: "all");
        var analysisComment = Markers.Wrap(analysis, Markers.Analysis);
        kit.CodeHost.AddBotComment(12, analysisComment);
    }

    // ---- analysis ----

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task A_complex_analysis_over_all_components_with_a_spec_per_component_is_posted(string layout)
    {
        using var kit = new JobTestKit(project: Layout(layout));
        kit.CodeHost.AddIssue(7, "Export", "body", "ai");
        var names = CodeNames(kit.Project);
        var table = Samples.Breakdown(names[0], names[1], names[0]);
        var analysis = Samples.Analysis("ready", "complex", table, components: "all");
        kit.Copilot.StdOut = Samples.AgentOutput(analysis, Samples.SpecFor(names));

        var outcome = await AnalyzeJob(kit).RunAsync(kit.Request(JobType.AnalyzeIssue, 7), kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(2, kit.CodeHost.CommentsOf(7).Count);
        Assert.Contains(Labels.Complex, kit.CodeHost.LabelsOf(7));
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task A_simple_analysis_with_one_component_passes(string layout)
    {
        using var kit = new JobTestKit(project: Layout(layout));
        var first = CodeNames(kit.Project)[0];
        kit.CodeHost.AddIssue(7, "Export", "body", "ai");
        var analysis = Samples.Analysis("ready", "simple", components: first);
        kit.Copilot.StdOut = Samples.AgentOutput(analysis, Samples.SpecFor(first));

        var outcome = await AnalyzeJob(kit).RunAsync(kit.Request(JobType.AnalyzeIssue, 7), kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Contains(Labels.Simple, kit.CodeHost.LabelsOf(7));
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task An_analysis_that_breaks_the_component_rules_fails_before_anything_is_posted(string layout)
    {
        using var kit = new JobTestKit(project: Layout(layout));
        var spec = Samples.SpecFor(CodeNames(kit.Project));
        string[] badAnalyses =
        [
            Samples.Analysis("ready", "simple", components: "all"),         // two components, but simple
            Samples.Analysis("ready", "complex", components: "shop"),       // not a component
            Samples.Analysis("ready", "complex", components: "issues"),     // no code component of that name
            Samples.Analysis("ready", "complex", components: "unknown"),    // ready needs names
        ];
        kit.CodeHost.AddIssue(7, "Export", "body", "ai");

        foreach (var analysis in badAnalyses)
        {
            kit.Copilot.StdOut = Samples.AgentOutput(analysis, spec);

            var exception = await Assert.ThrowsAsync<JobFailedException>(
                () => AnalyzeJob(kit).RunAsync(kit.Request(JobType.AnalyzeIssue, 7), kit.Run, default));

            Assert.Contains("The analysis has the wrong format", exception.Message);
        }

        Assert.Empty(kit.CodeHost.CommentsOf(7));
        Assert.Equal(["ai"], kit.CodeHost.LabelsOf(7));
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task A_spec_step_with_a_bad_component_fails_before_anything_is_posted(string layout)
    {
        using var kit = new JobTestKit(project: Layout(layout));
        var first = CodeNames(kit.Project)[0];
        kit.CodeHost.AddIssue(7, "Export", "body", "ai");
        var analysis = Samples.Analysis("ready", "simple", components: first);
        kit.Copilot.StdOut = Samples.AgentOutput(analysis, Samples.SpecFor("shop"));

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => AnalyzeJob(kit).RunAsync(kit.Request(JobType.AnalyzeIssue, 7), kit.Run, default));

        Assert.Contains("The SPEC has the wrong format", exception.Message);
        Assert.Empty(kit.CodeHost.CommentsOf(7));
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task Questions_open_may_leave_the_components_unknown(string layout)
    {
        using var kit = new JobTestKit(project: Layout(layout));
        kit.CodeHost.AddIssue(7, "Export", "body", "ai");
        kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("questions-open", "simple"));

        var outcome = await AnalyzeJob(kit).RunAsync(kit.Request(JobType.AnalyzeIssue, 7), kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Contains(Labels.QuestionsOpen, kit.CodeHost.LabelsOf(7));
    }

    // ---- breakdown ----

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task Sub_issues_are_created_only_in_the_issues_repo_with_component_and_parent_lines(string layout)
    {
        var project = Layout(layout);
        using var kit = new JobTestKit(project: project);
        var names = CodeNames(project);
        PrepareParent(kit, Samples.Breakdown(names[0], names[1], names[0]));

        var outcome = await BreakdownJob(kit).RunAsync(kit.Request(JobType.BreakdownIssue, 12), kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal([project.IssuesRepo], kit.CodeHost.ReposOf("create-issue"));

        var subIssues = kit.CodeHost.Issues.Values
            .Where(issue => issue.HasLabel(Labels.SubIssue))
            .OrderBy(issue => issue.Number)
            .ToList();
        Assert.Equal(3, subIssues.Count);
        Assert.Equal(
            [names[0], names[1], names[0]],
            subIssues.Select(issue => SubIssueBody.Parse(issue.Body)!.Component));
        Assert.All(subIssues, issue => Assert.Contains("Parent: #12", issue.Body));
        Assert.All(subIssues, issue => Assert.Matches(@"(?m)^Component: \S+\r?$", issue.Body!));
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task A_feature_branch_is_made_once_in_every_repo_that_has_a_sub_issue(string layout)
    {
        var project = Layout(layout);
        using var kit = new JobTestKit(project: project);
        var names = CodeNames(project);
        PrepareParent(kit, Samples.Breakdown(names[0], names[1], names[0]));

        await BreakdownJob(kit).RunAsync(kit.Request(JobType.BreakdownIssue, 12), kit.Run, default);

        // Components in two repos get a branch each; components that share a repo share one branch.
        var expectedClones = names.Select(name => CloneOf(kit, name)).Distinct().Order();
        Assert.Equal(expectedClones, kit.Git.PushLog.Select(push => push.Directory).Order());
        Assert.All(kit.Git.PushLog, push => Assert.Equal(("origin/main", "feat/issue-12"), (push.Source, push.Branch)));
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task The_parent_comment_lists_each_sub_issue_with_its_component(string layout)
    {
        var project = Layout(layout);
        using var kit = new JobTestKit(project: project);
        var names = CodeNames(project);
        PrepareParent(kit, Samples.Breakdown(names[0], names[1], names[0]));

        await BreakdownJob(kit).RunAsync(kit.Request(JobType.BreakdownIssue, 12), kit.Run, default);

        var comment = kit.CodeHost.CommentsOf(12).Last().Body!;
        Assert.Contains($"Export entity [{names[0]}]", comment);
        Assert.Contains($"Export command [{names[1]}]", comment);
        Assert.Contains($"Export API [{names[0]}] (a developer builds this one)", comment);
        Assert.Equal(3, SubIssueBody.FindList(kit.CodeHost.CommentsOf(12))!.Count);
        Assert.Contains(Labels.Parent, kit.CodeHost.LabelsOf(12));
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task A_breakdown_row_with_an_unknown_component_creates_nothing(string layout)
    {
        var project = Layout(layout);
        using var kit = new JobTestKit(project: project);
        var names = CodeNames(project);
        PrepareParent(kit, Samples.Breakdown(names[0], "shop", names[1]));

        var outcome = await BreakdownJob(kit).RunAsync(kit.Request(JobType.BreakdownIssue, 12), kit.Run, default);

        Assert.Equal("rejected", outcome.Result);
        Assert.Contains("row 2", kit.CodeHost.CommentsOf(12).Last().Body);
        Assert.DoesNotContain(Labels.Breakdown, kit.CodeHost.LabelsOf(12));
        Assert.Empty(kit.CodeHost.ReposOf("create-issue"));
        Assert.Empty(kit.Git.PushLog);
    }

    [Fact]
    public async Task Only_repos_with_a_sub_issue_get_a_feature_branch()
    {
        using var kit = new JobTestKit(project: TestProjects.SeparateIssuesRepo());
        PrepareParent(kit, Samples.Breakdown("backend", "backend", "backend"));

        await BreakdownJob(kit).RunAsync(kit.Request(JobType.BreakdownIssue, 12), kit.Run, default);

        var push = Assert.Single(kit.Git.PushLog);
        Assert.Equal(CloneOf(kit, "backend"), push.Directory);
    }

    [Fact]
    public async Task A_feature_branch_that_already_exists_is_kept_in_every_repo()
    {
        using var kit = new JobTestKit(project: TestProjects.SeparateIssuesRepo());
        PrepareParent(kit, Samples.Breakdown("backend", "frontend", "backend"));
        kit.Git.Refs["origin/feat/issue-12"] = "saved01";   // a retry after an interrupted run

        var outcome = await BreakdownJob(kit).RunAsync(kit.Request(JobType.BreakdownIssue, 12), kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Empty(kit.Git.PushLog);
        Assert.Equal(3, kit.CodeHost.Issues.Values.Count(issue => issue.HasLabel(Labels.SubIssue)));
    }

    [Fact]
    public async Task The_workspace_has_the_whole_project_so_the_breakdown_needs_no_single_code_component()
    {
        var project = TestProjects.SeparateIssuesRepo();
        using var kit = new JobTestKit(project: project);
        PrepareParent(kit, Samples.Breakdown("backend", "frontend", "backend"));

        await BreakdownJob(kit).RunAsync(kit.Request(JobType.BreakdownIssue, 12), kit.Run, default);

        var workspaceRequest = kit.Workspace.Requests.Single();
        Assert.Same(project, workspaceRequest.Project);
        Assert.False(workspaceRequest.Writable);
    }
}
