using AiRunner.Jobs;
using AiRunner.Tests.Fakes;
using static AiRunner.Tests.Fakes.DeliveryLayouts;

namespace AiRunner.Tests.Jobs;

/// <summary>
/// Implement, sequencing, finishing, review and review fix across components and repos. Same assertions for the
/// three layouts; each has two code components.
/// </summary>
public sealed class ComponentDeliveryTests
{
    // ---- implement ----

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_simple_issue_is_built_in_the_component_its_analysis_names_and_the_pr_is_opened_only_there(
        string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (first, second) = Pair(kit.Project);
        kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        AddAnalysis(kit, 12, "simple", second.Name);
        kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.SpecFor(second.Name), Markers.Spec));

        var outcome = await ImplementJob(kit).RunAsync(kit.Request(JobType.ImplementIssue, 12), kit.Run, default);

        Assert.Equal("ok", outcome.Result);

        var workspaceRequest = kit.Workspace.Requests.Single();
        Assert.Equal(second.Repo, workspaceRequest.TargetRepo);
        Assert.Equal(second.Name, workspaceRequest.TargetComponent);
        Assert.True(workspaceRequest.Writable);

        // Branch and PR are in the repo of the component; nothing is pushed or opened anywhere else.
        Assert.All(kit.Git.PushLog, push => Assert.Equal(CloneOf(kit, second), push.Directory));
        Assert.Equal([second.Repo], kit.GitBucket.ReposOf("create-pull"));
        Assert.Equal(first.Repo == second.Repo, kit.Git.PushLog.All(push => push.Directory == CloneOf(kit, first)));

        var pull = kit.GitBucket.Pulls.Single();
        Assert.Contains($"Issue: {kit.Project.IssuesRepo}#12", pull.Body);
        Assert.DoesNotContain("Closes", pull.Body);

        // The issue, its label and its comments stay in the issues repo.
        Assert.Empty(kit.GitBucket.ReposOf("create-issue"));
        Assert.Contains(kit.Project.IssuesRepo, kit.GitBucket.ReposOf("comment"));
        Assert.DoesNotContain(second.Repo == kit.Project.IssuesRepo ? "-" : second.Repo, kit.GitBucket.ReposOf("comment"));
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_sub_issue_is_built_in_the_component_on_its_component_line(string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (_, second) = Pair(kit.Project);
        kit.GitBucket.AddIssue(
            21, "#12.1 Task", SubIssueBodyFor(12, 1, second.Name), "ai", Labels.SubIssue, Labels.Ready, Labels.Simple, Labels.Implement);

        var outcome = await ImplementJob(kit).RunAsync(kit.Request(JobType.ImplementIssue, 21), kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        var workspaceRequest = kit.Workspace.Requests.Single();
        Assert.Equal((second.Repo, second.Name), (workspaceRequest.TargetRepo, workspaceRequest.TargetComponent));
        Assert.Equal(("origin/feat/issue-12", "ai/issue-12-1"), (workspaceRequest.StartPoint, workspaceRequest.NewBranch));
        Assert.Equal([second.Repo], kit.GitBucket.ReposOf("create-pull"));

        var pull = kit.GitBucket.Pulls.Single();
        Assert.Equal(("ai/issue-12-1", "feat/issue-12"), (pull.Head.Ref, pull.Base.Ref));
        Assert.Contains($"Issue: {kit.Project.IssuesRepo}#21", pull.Body);
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_sub_issue_waits_for_the_previous_one_in_the_repo_of_the_previous_one(string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (first, second) = Pair(kit.Project);
        kit.GitBucket.AddIssue(20, "#12.1", SubIssueBodyFor(12, 1, first.Name), "ai", Labels.SubIssue, Labels.Simple);
        kit.GitBucket.AddIssue(
            21, "#12.2", SubIssueBodyFor(12, 2, second.Name), "ai", Labels.SubIssue, Labels.Ready, Labels.Simple, Labels.Implement);
        kit.GitBucket.AddBotComment(12, SubIssueBody.ListMarker([20, 21]));
        kit.GitBucket.AddIssue(12, "Parent", null, "ai", Labels.Parent);

        // Merged in the repo of the second component only: that is not where the first sub-issue's PR belongs.
        // In the monorepo both components share the repo, so there the PR is in the right place anyway.
        kit.GitBucket.AddPull(
            50, "ai/issue-12-1", "feat/issue-12", merged: true, state: "closed", repo: second.Repo);
        var outcome = await ImplementJob(kit).RunAsync(kit.Request(JobType.ImplementIssue, 21), kit.Run, default);

        if (first.Repo == second.Repo)
        {
            Assert.Equal("ok", outcome.Result);
            return;
        }

        Assert.Equal("rejected", outcome.Result);
        Assert.Contains("is not merged yet", kit.GitBucket.CommentsOf(21).Last().Body);

        kit.GitBucket.AddPull(
            50, "ai/issue-12-1", "feat/issue-12", merged: true, state: "closed", repo: first.Repo);
        await kit.GitBucket.AddLabelsAsync(kit.Project.IssuesRepo, 21, [Labels.Implement], default);
        var again = await ImplementJob(kit).RunAsync(kit.Request(JobType.ImplementIssue, 21), kit.Run, default);

        Assert.Equal("ok", again.Result);
    }

    [Fact]
    public async Task Edits_outside_the_component_and_in_never_edit_files_are_undone_and_listed_in_the_pr()
    {
        using var kit = new JobTestKit(
            settings => settings.NeverEdit = ["Jenkinsfile", "migrations/"],
            Create("monorepo"));
        var (web, api) = Pair(kit.Project);
        kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        AddAnalysis(kit, 12, "simple", api.Name);
        kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.SpecFor(api.Name), Markers.Spec));
        var clone = CloneOf(kit, api);
        string[] changes =
        [
            FileIn(api, "src/Export.cs"),
            FileIn(web, "src/Page.ts"),
            "Jenkinsfile",
            FileIn(api, "migrations/001.sql"),
        ];
        kit.Git.Changed[clone] = [.. changes];

        var outcome = await ImplementJob(kit).RunAsync(kit.Request(JobType.ImplementIssue, 12), kit.Run, default);

        // An undo alone starts no fix round and does not stop the PR.
        Assert.Equal("ok", outcome.Result);
        Assert.Single(kit.Copilot.Requests);
        Assert.Equal(
            [FileIn(web, "src/Page.ts"), "Jenkinsfile", FileIn(api, "migrations/001.sql")],
            kit.Git.Restored.Select(restored => restored.Path));
        Assert.Equal([FileIn(api, "src/Export.cs")], kit.Git.Changed[clone]);

        var body = kit.GitBucket.Pulls.Single().Body!;
        Assert.Contains("Changes undone by ai-runner", body);
        Assert.Contains(FileIn(web, "src/Page.ts"), body);
        Assert.Contains("matches never_edit", body);
        Assert.DoesNotContain(FileIn(api, "src/Export.cs"), body);
    }

    [Fact]
    public async Task Files_written_by_the_checks_outside_the_component_are_undone_before_the_commit()
    {
        using var kit = new JobTestKit(project: Create("monorepo"));
        var (web, api) = Pair(kit.Project);
        api.Commands.Build = "build-it";
        kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        AddAnalysis(kit, 12, "simple", api.Name);
        kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.SpecFor(api.Name), Markers.Spec));
        var clone = CloneOf(kit, api);
        kit.Git.Changed[clone] = [FileIn(api, "src/Export.cs")];

        // The agent stays inside its folder; the build then writes a file in the other app.
        kit.Processes.OnCommand = _ => kit.Git.Changed[clone].Add(FileIn(web, "package-lock.json"));

        var outcome = await ImplementJob(kit).RunAsync(kit.Request(JobType.ImplementIssue, 12), kit.Run, default);

        Assert.Equal("ok", outcome.Result);

        // Every round of checks writes the file again (a missing PR_BODY.md starts a fix round) and every time it goes.
        Assert.NotEmpty(kit.Git.Restored);
        Assert.All(kit.Git.Restored, restored => Assert.Equal((clone, FileIn(web, "package-lock.json")), restored));
        Assert.Equal([FileIn(api, "src/Export.cs")], kit.Git.Changed[clone]);
        Assert.Contains(FileIn(web, "package-lock.json"), kit.GitBucket.Pulls.Single().Body!);
    }

    [Fact]
    public async Task A_change_in_a_sibling_repo_is_undone_and_never_pushed()
    {
        using var kit = new JobTestKit(project: Create("separate-issues-repo"));
        var (backend, frontend) = Pair(kit.Project);
        kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        AddAnalysis(kit, 12, "simple", backend.Name);
        kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.SpecFor(backend.Name), Markers.Spec));
        kit.Git.Changed[CloneOf(kit, frontend)] = ["src/api-client.ts"];
        kit.Git.Changed[CloneOf(kit, backend)] = ["src/Export.cs"];

        await ImplementJob(kit).RunAsync(kit.Request(JobType.ImplementIssue, 12), kit.Run, default);

        Assert.Equal([(CloneOf(kit, frontend), "src/api-client.ts")], kit.Git.Restored);
        Assert.Contains(frontend.Repo, kit.GitBucket.Pulls.Single().Body!);
        Assert.All(kit.Git.PushLog, push => Assert.Equal(CloneOf(kit, backend), push.Directory));
    }

    [Fact]
    public async Task The_undone_list_goes_to_the_agent_when_a_fix_round_happens_anyway()
    {
        using var kit = new JobTestKit(project: Create("monorepo"));
        var (web, api) = Pair(kit.Project);
        kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        AddAnalysis(kit, 12, "simple", api.Name);
        kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.SpecFor(api.Name), Markers.Spec));
        kit.Git.Changed[CloneOf(kit, api)] = [FileIn(web, "src/Page.ts"), FileIn(api, "src/Export.cs")];
        kit.Processes.ExitCodeSequence["make test"] = new Queue<int>([1, 0]);

        var outcome = await ImplementJob(kit).RunAsync(kit.Request(JobType.ImplementIssue, 12), kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(2, kit.Copilot.Requests.Count);
        var fixFile = File.ReadAllText(Path.Combine(kit.Run.Dir, "fix-2.md"));
        Assert.Contains("Changes the runner undid", fixFile);
        Assert.Contains(FileIn(web, "src/Page.ts"), fixFile);
    }

    // ---- sequencing and closing after merge ----

    private static void ArrangeParent(JobTestKit kit, string[] components)
    {
        kit.GitBucket.AddIssue(12, "Export", "body", "ai", Labels.Parent);
        List<int> numbers = [];
        for (var index = 0; index < components.Length; index++)
        {
            var number = 20 + index;
            numbers.Add(number);
            var labels = new List<string> { "ai", Labels.SubIssue, Labels.Ready, Labels.Simple };
            if (index == 0)
            {
                labels.Add(Labels.Implementing);
            }

            kit.GitBucket.AddIssue(number, $"#12.{index + 1}", SubIssueBodyFor(12, index + 1, components[index]), [.. labels]);
        }

        kit.GitBucket.AddBotComment(12, SubIssueBody.ListMarker(numbers));
    }

    private static void Merge(JobTestKit kit, int number, int order, ComponentConfig component) =>
        kit.GitBucket.AddPull(
            number, $"ai/issue-12-{order}", "feat/issue-12", merged: true, state: "closed", title: $"#2{order - 1}: task", repo: component.Repo);

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_merged_sub_issue_pr_is_commented_and_closed_in_the_issues_repo_and_the_next_one_starts(
        string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (first, second) = Pair(kit.Project);
        ArrangeParent(kit, [first.Name, second.Name, first.Name]);
        Merge(kit, 50, 1, first);

        var outcome = await new SequenceSubIssuesJob(kit.GitBucket, kit.Loader)
            .RunAsync(kit.Request(JobType.SequenceSubIssues, 12), kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.False(kit.GitBucket.Issues[20].IsOpen());
        Assert.DoesNotContain(Labels.Implementing, kit.GitBucket.LabelsOf(20));
        Assert.Contains("is merged into `feat/issue-12`", kit.GitBucket.CommentsOf(20).Last().Body);
        Assert.Contains(JobSupport.PullRef(50, kit.Project.IssuesRepo, first.Repo), kit.GitBucket.CommentsOf(20).Last().Body);
        Assert.Equal([kit.Project.IssuesRepo], kit.GitBucket.ReposOf("close-issue"));

        Assert.Contains(Labels.Implement, kit.GitBucket.LabelsOf(21));
        Assert.True(kit.GitBucket.Issues[21].IsOpen());
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_sub_issue_pr_closed_without_a_merge_stops_the_sub_issue_and_it_is_not_started_again(
        string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (first, second) = Pair(kit.Project);
        ArrangeParent(kit, [first.Name, second.Name, first.Name]);
        kit.GitBucket.AddPull(50, "ai/issue-12-1", "feat/issue-12", state: "closed", repo: first.Repo);
        var job = new SequenceSubIssuesJob(kit.GitBucket, kit.Loader);
        var request = kit.Request(JobType.SequenceSubIssues, 12);

        var outcome = await job.RunAsync(request, kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.True(kit.GitBucket.Issues[20].IsOpen());
        Assert.Contains(Labels.Failed, kit.GitBucket.LabelsOf(20));
        Assert.DoesNotContain(Labels.Implementing, kit.GitBucket.LabelsOf(20));
        Assert.Contains("closed without a merge", kit.GitBucket.CommentsOf(20).Last().Body);
        Assert.DoesNotContain(Labels.Implement, kit.GitBucket.LabelsOf(21));

        // The next poll finds nothing new: no second comment, no restart.
        var commentCount = kit.GitBucket.CommentsOf(20).Count;
        var again = await job.RunAsync(request, kit.Run, default);

        Assert.True(again.Quiet);
        Assert.Equal(commentCount, kit.GitBucket.CommentsOf(20).Count);
        Assert.DoesNotContain(Labels.Implement, kit.GitBucket.LabelsOf(20));
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task Final_prs_are_opened_once_per_repo_listed_in_one_comment_and_the_parent_closes_after_the_merges(
        string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (first, second) = Pair(kit.Project);
        ArrangeParent(kit, [first.Name, second.Name, first.Name]);
        Merge(kit, 50, 1, first);
        Merge(kit, 51, 2, second);
        Merge(kit, 52, 3, first);
        var job = new SequenceSubIssuesJob(kit.GitBucket, kit.Loader);
        var request = kit.Request(JobType.SequenceSubIssues, 12);
        var repos = new[] { first.Repo, second.Repo }.Distinct().Order().ToList();

        // 1. Every sub-issue is closed, and each repo with sub-issues gets one final PR.
        await job.RunAsync(request, kit.Run, default);

        Assert.All([20, 21, 22], number => Assert.False(kit.GitBucket.Issues[number].IsOpen()));
        var finalPulls = kit.GitBucket.Pulls.Where(pull => pull.Head.Ref == "feat/issue-12").ToList();
        Assert.Equal(repos.Count, finalPulls.Count);
        Assert.Equal(repos, kit.GitBucket.ReposOf("create-pull").Order());
        Assert.All(finalPulls, pull => Assert.Equal("main", pull.Base.Ref));
        Assert.All(finalPulls, pull => Assert.Contains($"Issue: {kit.Project.IssuesRepo}#12", pull.Body));
        Assert.All(finalPulls, pull => Assert.DoesNotContain("Closes", pull.Body));

        // One parent comment lists all the final PRs.
        var listComments = kit.GitBucket.CommentsOf(12).Where(comment => comment.Body!.Contains(SubIssueBody.FinalPrsMarker)).ToList();
        var list = Assert.Single(listComments).Body!;
        Assert.All(finalPulls, pull => Assert.Contains($"#{pull.Number}", list));

        // 2. Nothing more happens while the final PRs are open.
        var waiting = await job.RunAsync(request, kit.Run, default);
        Assert.True(waiting.Quiet);
        Assert.Equal(repos.Count, kit.GitBucket.Pulls.Count(pull => pull.Head.Ref == "feat/issue-12"));
        Assert.True(kit.GitBucket.Issues[12].IsOpen());

        // 3. One final PR merged is not enough (when there are two); all of them close the parent.
        for (var index = 0; index < finalPulls.Count; index++)
        {
            var pull = finalPulls[index];
            var repo = kit.GitBucket.PullRepos[pull.Number];
            kit.GitBucket.AddPull(pull.Number, pull.Head.Ref, pull.Base.Ref, merged: true, state: "closed", repo: repo);

            var outcome = await job.RunAsync(request, kit.Run, default);

            var isLast = index == finalPulls.Count - 1;
            Assert.Equal(!isLast, outcome.Quiet);
            Assert.Equal(!isLast, kit.GitBucket.Issues[12].IsOpen());
        }

        Assert.Contains("All final PRs are merged", kit.GitBucket.CommentsOf(12).Last().Body);
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_final_pr_closed_without_a_merge_is_replaced_and_listed_again(string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (first, second) = Pair(kit.Project);
        ArrangeParent(kit, [first.Name, second.Name, first.Name]);
        Merge(kit, 50, 1, first);
        Merge(kit, 51, 2, second);
        Merge(kit, 52, 3, first);
        var job = new SequenceSubIssuesJob(kit.GitBucket, kit.Loader);
        var request = kit.Request(JobType.SequenceSubIssues, 12);
        await job.RunAsync(request, kit.Run, default);
        var finalPull = kit.GitBucket.Pulls.First(pull => pull.Head.Ref == "feat/issue-12");
        var repo = kit.GitBucket.PullRepos[finalPull.Number];
        var finalCount = kit.GitBucket.Pulls.Count(pull => pull.Head.Ref == "feat/issue-12");

        // Someone closes a final PR without merging it.
        kit.GitBucket.AddPull(finalPull.Number, finalPull.Head.Ref, finalPull.Base.Ref, state: "closed", repo: repo);
        var outcome = await job.RunAsync(request, kit.Run, default);

        Assert.False(outcome.Quiet);
        var open = kit.GitBucket.Pulls.Where(pull => pull.Head.Ref == "feat/issue-12" && pull.IsOpen()).ToList();
        Assert.Equal(finalCount, open.Count);
        Assert.DoesNotContain(open, pull => pull.Number == finalPull.Number);
        var lists = kit.GitBucket.CommentsOf(12).Where(comment => comment.Body!.Contains(SubIssueBody.FinalPrsMarker));
        Assert.Equal(2, lists.Count());
    }

    // ---- finishing a simple issue ----

    private static FinishIssueJob FinishJob(JobTestKit kit) => new(kit.GitBucket, kit.Loader);

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_simple_issue_is_commented_and_closed_after_its_pr_is_merged(string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (_, second) = Pair(kit.Project);
        ArrangeSimpleIssue(kit, second);
        kit.GitBucket.AddPull(30, "ai/issue-12", "main", merged: true, state: "closed", repo: second.Repo);

        var outcome = await FinishJob(kit).RunAsync(kit.Request(JobType.FinishIssue, 12), kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(0, outcome.CopilotCalls);
        Assert.False(kit.GitBucket.Issues[12].IsOpen());
        Assert.DoesNotContain(Labels.Implementing, kit.GitBucket.LabelsOf(12));
        Assert.Contains(JobSupport.PullRef(30, kit.Project.IssuesRepo, second.Repo), kit.GitBucket.CommentsOf(12).Last().Body);
        Assert.Equal([kit.Project.IssuesRepo], kit.GitBucket.ReposOf("close-issue"));
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task An_open_pr_or_no_pr_leaves_the_issue_alone(string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (_, second) = Pair(kit.Project);
        ArrangeSimpleIssue(kit, second);

        var withoutPull = await FinishJob(kit).RunAsync(kit.Request(JobType.FinishIssue, 12), kit.Run, default);
        kit.GitBucket.AddPull(30, "ai/issue-12", "main", repo: second.Repo);
        var withOpenPull = await FinishJob(kit).RunAsync(kit.Request(JobType.FinishIssue, 12), kit.Run, default);

        Assert.True(withoutPull.Quiet);
        Assert.True(withOpenPull.Quiet);
        Assert.True(kit.GitBucket.Issues[12].IsOpen());
        Assert.Contains(Labels.Implementing, kit.GitBucket.LabelsOf(12));
        Assert.Single(kit.GitBucket.CommentsOf(12));   // only the analysis
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_pr_closed_without_a_merge_frees_the_issue_but_does_not_close_it(string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (_, second) = Pair(kit.Project);
        ArrangeSimpleIssue(kit, second);
        kit.GitBucket.AddPull(30, "ai/issue-12", "main", state: "closed", repo: second.Repo);

        var outcome = await FinishJob(kit).RunAsync(kit.Request(JobType.FinishIssue, 12), kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.True(kit.GitBucket.Issues[12].IsOpen());
        Assert.DoesNotContain(Labels.Implementing, kit.GitBucket.LabelsOf(12));
        Assert.Contains("closed without a merge", kit.GitBucket.CommentsOf(12).Last().Body);
    }

    [Fact]
    public async Task Only_the_repo_of_the_component_is_asked_for_the_pr()
    {
        using var kit = new JobTestKit(project: Create("separate-issues-repo"));
        var (backend, frontend) = Pair(kit.Project);
        ArrangeSimpleIssue(kit, backend);
        kit.GitBucket.AddPull(30, "ai/issue-12", "main", merged: true, state: "closed", repo: frontend.Repo);

        var outcome = await FinishJob(kit).RunAsync(kit.Request(JobType.FinishIssue, 12), kit.Run, default);

        Assert.True(outcome.Quiet);
        Assert.True(kit.GitBucket.Issues[12].IsOpen());
        Assert.Equal([backend.Repo], kit.GitBucket.ReposOf("pulls"));
    }

    // ---- review ----

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task The_review_finds_the_spec_in_the_issues_repo_and_sees_the_siblings_read_only(string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (_, second) = Pair(kit.Project);
        kit.GitBucket.AddIssue(12, "Export", null, "ai");
        kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.SpecFor(second.Name), Markers.Spec));
        kit.GitBucket.AddPull(
            30, "ai/issue-12", "main", sha: "sha1", body: $"Adds export.\n\nIssue: {kit.Project.IssuesRepo}#12", labels: "ai", repo: second.Repo);
        kit.Copilot.StdOut = Markers.Wrap(Samples.Review(), Markers.Review);
        var request = new JobRequest(JobType.ReviewPullRequest, second.Repo, 30, "sha1");

        var outcome = await ReviewJob(kit).RunAsync(request, kit.Run, default);

        Assert.Equal("ok", outcome.Result);

        // The SPEC is read from the issue's own repo.
        Assert.Contains(kit.Project.IssuesRepo, kit.GitBucket.ReposOf("comments-read"));
        var context = File.ReadAllText(Path.Combine(kit.Workspace.Dir, "context.md"));
        Assert.Contains($"Linked issue {kit.Project.IssuesRepo}#12", context);
        Assert.Contains("SPEC - issue #12", context);

        // The PR repo is the target; nothing is writable.
        var workspaceRequest = kit.Workspace.Requests.Single();
        Assert.Equal(second.Repo, workspaceRequest.TargetRepo);
        Assert.False(workspaceRequest.Writable);
        Assert.Equal(["write", "shell"], kit.Copilot.Requests.Single().DenyTools);
    }

    // ---- review fix ----

    private static GbComment ArrangeFixPull(JobTestKit kit, ComponentConfig pullComponent, string? linkLine)
    {
        kit.GitBucket.AddPull(
            30,
            "ai/issue-12",
            "main",
            sha: "sha1",
            body: linkLine is null ? "Adds export." : $"Adds export.\n\n{linkLine}",
            labels: "ai",
            repo: pullComponent.Repo);
        kit.Git.Refs["origin/ai/issue-12"] = "sha1";
        kit.GitBucket.AddBotComment(30, Markers.Wrap(Samples.Review(), Markers.Review));

        return kit.GitBucket.AddHumanComment(30, "@ai 1: fix, 2: skip");
    }

    private static Task<JobOutcome> RunFix(JobTestKit kit, ComponentConfig pullComponent, GbComment trigger)
    {
        var request = new JobRequest(JobType.FixReview, pullComponent.Repo, 30, trigger.Id.ToString());

        return FixJob(kit).RunAsync(request, kit.Run, default);
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_fix_changes_only_the_component_the_linked_issue_is_for(string layout)
    {
        using var kit = new JobTestKit(project: Create(layout));
        var (first, second) = Pair(kit.Project);
        kit.GitBucket.AddIssue(12, "Export", null, "ai");
        kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.SpecFor(second.Name), Markers.Spec));
        AddAnalysis(kit, 12, "simple", second.Name);
        var trigger = ArrangeFixPull(kit, second, $"Issue: {kit.Project.IssuesRepo}#12");

        // The agent touched a file of each component (or, in separate repos, a file in each repo).
        var secondClone = CloneOf(kit, second);
        var firstClone = CloneOf(kit, first);
        var kept = FileIn(second, "src/Fixed.cs");
        var other = FileIn(first, "src/Other.cs");
        kit.Git.Changed[secondClone] = [kept];
        if (firstClone == secondClone)
        {
            kit.Git.Changed[secondClone].Add(other);
        }
        else
        {
            kit.Git.Changed[firstClone] = [other];
        }

        var outcome = await RunFix(kit, second, trigger);

        Assert.Equal("ok", outcome.Result);
        var workspaceRequest = kit.Workspace.Requests.Single();
        Assert.Equal((second.Repo, second.Name), (workspaceRequest.TargetRepo, workspaceRequest.TargetComponent));
        Assert.Equal([(firstClone, other)], kit.Git.Restored);
        Assert.Equal([kept], kit.Git.Changed[secondClone]);
        Assert.Contains("Changes undone by ai-runner", kit.GitBucket.CommentsOf(30).Last().Body);
    }

    [Fact]
    public async Task A_sub_issue_link_wins_with_its_component_line()
    {
        using var kit = new JobTestKit(project: Create("monorepo"));
        var (web, api) = Pair(kit.Project);
        kit.GitBucket.AddIssue(
            21, "#12.1", SubIssueBodyFor(12, 1, web.Name), "ai", Labels.SubIssue, Labels.Ready, Labels.Simple);
        var trigger = ArrangeFixPull(kit, web, $"Issue: {kit.Project.IssuesRepo}#21");
        kit.Git.Changed[CloneOf(kit, web)] = [FileIn(web, "a.ts"), FileIn(api, "b.cs")];

        await RunFix(kit, web, trigger);

        Assert.Equal(web.Name, kit.Workspace.Requests.Single().TargetComponent);
        Assert.Equal([(CloneOf(kit, web), FileIn(api, "b.cs"))], kit.Git.Restored);
    }

    [Fact]
    public async Task A_link_to_a_component_in_another_repo_fails_instead_of_widening_the_fix()
    {
        using var kit = new JobTestKit(project: Create("multi-repo"));
        var (first, second) = Pair(kit.Project);
        kit.GitBucket.AddIssue(12, "Export", null, "ai");
        AddAnalysis(kit, 12, "simple", second.Name);
        var trigger = ArrangeFixPull(kit, first, $"Issue: {kit.Project.IssuesRepo}#12");

        var exception = await Assert.ThrowsAsync<JobFailedException>(() => RunFix(kit, first, trigger));

        Assert.Contains("none of them is in", exception.Message);
        Assert.Empty(kit.Copilot.Requests);
    }

    [Fact]
    public async Task Every_writable_component_is_checked_and_needs_a_check_command()
    {
        using var kit = new JobTestKit(project: Create("monorepo"));
        var (web, api) = Pair(kit.Project);
        var trigger = ArrangeFixPull(kit, web, null);

        var outcome = await RunFix(kit, web, trigger);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(2, kit.Processes.Commands.Count(command => command.Contains("make build")));
        Assert.Equal(2, kit.Processes.Commands.Count(command => command.Contains("make test")));

        // A component without any check command refuses the fix, because its changes could not be checked.
        api.Commands = new CommandsConfig();
        var secondTrigger = kit.GitBucket.AddHumanComment(30, "@ai again");
        kit.Copilot.Requests.Clear();
        var refused = await RunFix(kit, web, secondTrigger);

        Assert.Equal("rejected", refused.Result);
        Assert.Empty(kit.Copilot.Requests);
    }

    [Fact]
    public async Task Without_a_link_every_code_component_of_the_pr_repo_may_change()
    {
        using var kit = new JobTestKit(project: Create("monorepo"));
        var (web, api) = Pair(kit.Project);
        var trigger = ArrangeFixPull(kit, web, null);
        kit.Git.Changed[CloneOf(kit, web)] = [FileIn(web, "a.ts"), FileIn(api, "b.cs"), "README.md"];

        var outcome = await RunFix(kit, web, trigger);

        Assert.Equal("ok", outcome.Result);
        Assert.Null(kit.Workspace.Requests.Single().TargetComponent);
        Assert.Equal([(CloneOf(kit, web), "README.md")], kit.Git.Restored);
    }
}
