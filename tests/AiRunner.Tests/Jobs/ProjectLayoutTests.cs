using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Jobs;

/// <summary>
/// What depends on where the repos are, run for the three layouts of the spec (multi-repo, separate issues repo,
/// monorepo) with the same assertions.
/// </summary>
public sealed class ProjectLayoutTests
{
    public static TheoryData<string> LayoutNames => ["multi-repo", "separate-issues-repo", "monorepo"];

    private static ProjectConfig Layout(string name) =>
        name switch
        {
            "multi-repo" => TestProjects.MultiRepo(),
            "separate-issues-repo" => TestProjects.SeparateIssuesRepo(),
            _ => TestProjects.Monorepo(),
        };

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task Issues_are_read_from_the_issues_repo_only_and_pulls_from_every_code_repo(string layout)
    {
        var project = Layout(layout);
        using var kit = new JobTestKit(project: project);
        var queue = new JobQueue(kit.State, NullLogger<JobQueue>.Instance);
        var poller = new Poller(
            kit.Config, kit.GitBucket, kit.Loader, kit.State, queue, kit.Identity, kit.Status, NullLogger<Poller>.Instance);

        await poller.PollOnceAsync(default);

        Assert.Equal([project.IssuesRepo], kit.GitBucket.ReposOf("issues"));
        Assert.Equal(
            project.CodeRepos.Order(),
            kit.GitBucket.ReposOf("pulls").Order());
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task Setup_labels_creates_every_label_in_every_repo_of_the_project(string layout)
    {
        var project = Layout(layout);
        using var kit = new JobTestKit(project: project);
        var setup = new LabelSetup(kit.Config, kit.GitBucket, NullLogger<LabelSetup>.Instance);

        await setup.RunAsync(default);

        Assert.Equal(project.Repos.Order(), kit.GitBucket.ReposOf("label").Order());

        var labelNames = kit.GitBucket.CreatedLabels
            .Where(created => created.Repo == project.IssuesRepo)
            .Select(created => created.Label.Name)
            .ToList();
        Assert.Equal(Labels.All("ai").Select(label => label.Name), labelNames);
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task Poller_plans_a_review_for_a_pr_in_any_code_repo_but_never_for_the_issues_repo(string layout)
    {
        var project = Layout(layout);
        using var kit = new JobTestKit(project: project);
        kit.GitBucket.AddPull(3, "ai/issue-7", "main", labels: ["ai"]);
        var queue = new JobQueue(kit.State, NullLogger<JobQueue>.Instance);
        var poller = new Poller(
            kit.Config, kit.GitBucket, kit.Loader, kit.State, queue, kit.Identity, kit.Status, NullLogger<Poller>.Instance);

        await poller.PollOnceAsync(default);

        var queued = kit.State.ListJobs(JobStatus.Pending).Select(job => job.Request).ToList();
        Assert.All(queued, request => Assert.Equal(JobType.ReviewPullRequest, request.Type));
        Assert.All(queued, request => Assert.Contains(request.Repo, project.CodeRepos));
        Assert.Equal(project.CodeRepos.Count, queued.Count);
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public async Task The_workspace_gets_every_repo_of_the_project_for_an_analysis(string layout)
    {
        var project = Layout(layout);
        using var kit = new JobTestKit(project: project);
        kit.GitBucket.AddIssue(7, "Export", "body", "ai");
        kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("questions-open", "simple"));
        var job = new AnalyzeIssueJob(
            kit.Config, kit.GitBucket, kit.Loader, kit.Workspace, kit.Copilot, kit.State, kit.Attachments);

        await job.RunAsync(kit.Request(JobType.AnalyzeIssue, 7), kit.Run, default);

        var workspaceRequest = kit.Workspace.Requests.Single();
        Assert.Same(project, workspaceRequest.Project);
        Assert.Equal(project.IssuesRepo, workspaceRequest.TargetRepo);
        Assert.False(workspaceRequest.Writable);

        // Copilot starts in the project folder, which holds the clones and the runner files.
        var copilotRequest = kit.Copilot.Requests.Single();
        Assert.Equal(kit.Workspace.Dir, copilotRequest.WorkDir);
        Assert.True(File.Exists(Path.Combine(kit.Workspace.Dir, "context.md")));
    }
}

/// <summary>Implement with the issue in one repo and the code in another.</summary>
public sealed class SeparateIssuesRepoImplementTests : IDisposable
{
    private const string IssuesRepo = "org/mbh-issues";
    private const string CodeRepo = "org/mbh-backend";

    private readonly JobTestKit _kit;

    public SeparateIssuesRepoImplementTests()
    {
        var project = new ProjectConfig
        {
            Name = "mbh",
            IssuesIn = "issues",
            Components =
            [
                new ComponentConfig { Name = "issues", Repo = IssuesRepo, Code = false },
                new ComponentConfig
                {
                    Name = "backend",
                    Repo = CodeRepo,
                    Commands = new CommandsConfig { Build = "make build", Test = "make test" },
                },
            ],
        };
        _kit = new JobTestKit(project: project);
        _kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        _kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.Spec, Markers.Spec));
    }

    public void Dispose() => _kit.Dispose();

    private ImplementIssueJob Job =>
        new(_kit.Config, _kit.GitBucket, _kit.Loader, _kit.Workspace, _kit.Git, _kit.Session, _kit.Attachments);

    [Fact]
    public async Task Branch_and_pr_are_in_the_code_repo_and_the_issue_stays_in_the_issues_repo()
    {
        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);

        var workspaceRequest = _kit.Workspace.Requests.Single();
        Assert.Equal(CodeRepo, workspaceRequest.TargetRepo);
        Assert.True(workspaceRequest.Writable);
        Assert.Equal("backend", workspaceRequest.TargetComponent);

        Assert.Equal([CodeRepo], _kit.GitBucket.ReposOf("create-pull"));
        Assert.Contains(IssuesRepo, _kit.GitBucket.ReposOf("comment"));
        Assert.DoesNotContain(CodeRepo, _kit.GitBucket.ReposOf("comment"));
        Assert.DoesNotContain(CodeRepo, _kit.GitBucket.ReposOf("create-issue"));
    }

    [Fact]
    public async Task Pr_body_links_the_issue_with_its_repo_and_never_closes_it()
    {
        await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        var body = _kit.GitBucket.Pulls.Single().Body!;
        Assert.Contains($"Issue: {IssuesRepo}#12", body);
        Assert.DoesNotContain("Closes", body);
    }

    [Fact]
    public async Task Checks_run_in_the_component_folder_and_the_agent_starts_in_the_project_folder()
    {
        await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal(_kit.Workspace.Dir, _kit.Copilot.Requests.Single().WorkDir);
        Assert.True(File.Exists(Path.Combine(_kit.Workspace.Dir, "plan.md")));
        Assert.Contains("Target component: `backend`", File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "plan.md")));
        Assert.Equal(2, _kit.Processes.Commands.Count);
    }
}

/// <summary>An issue with no component named, in a project that has several code components.</summary>
public sealed class AmbiguousTargetTests : IDisposable
{
    private readonly JobTestKit _kit = new(project: TestProjects.SeparateIssuesRepo());

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Implement_asks_for_the_analysis_to_run_again_instead_of_guessing()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        _kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.Spec, Markers.Spec));
        var job = new ImplementIssueJob(
            _kit.Config, _kit.GitBucket, _kit.Loader, _kit.Workspace, _kit.Git, _kit.Session, _kit.Attachments);

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains("more than one code component", exception.Message);
        Assert.Contains("@ai", exception.Message);
        Assert.Empty(_kit.Copilot.Requests);
        Assert.Empty(_kit.GitBucket.Pulls);
    }
}

/// <summary>Sequencing reads PRs from the code repo; with several code components it must not guess.</summary>
public sealed class AmbiguousSequenceTests : IDisposable
{
    private readonly JobTestKit _kit = new(project: TestProjects.SeparateIssuesRepo());

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Sequencing_with_several_code_components_reads_and_opens_nothing()
    {
        _kit.GitBucket.AddIssue(5, "Parent", "body", "ai", Labels.Parent);
        _kit.GitBucket.AddBotComment(5, SubIssueBody.ListMarker([6]));
        _kit.GitBucket.AddIssue(6, "Sub", "body", "ai", Labels.SubIssue);
        var job = new SequenceSubIssuesJob(_kit.GitBucket, _kit.Loader);

        var outcome = await job.RunAsync(_kit.Request(JobType.SequenceSubIssues, 5), _kit.Run, default);

        Assert.True(outcome.Quiet);
        Assert.Empty(_kit.GitBucket.ReposOf("pulls"));
        Assert.Empty(_kit.GitBucket.ReposOf("create-pull"));
    }
}

/// <summary>The trigger keyword and label of a project replace "@ai" and "ai".</summary>
public sealed class TriggerSettingsTests : IDisposable
{
    private readonly JobTestKit _kit = new(settings =>
    {
        settings.TriggerLabel = "bot";
        settings.TriggerKeyword = "@bot";
    });

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Poller_filters_on_the_trigger_label_of_the_project()
    {
        _kit.GitBucket.AddIssue(7, "Mine", null, "bot");
        _kit.GitBucket.AddIssue(8, "Not mine", null, "ai");
        var queue = new JobQueue(_kit.State, NullLogger<JobQueue>.Instance);
        var poller = new Poller(
            _kit.Config, _kit.GitBucket, _kit.Loader, _kit.State, queue, _kit.Identity, _kit.Status, NullLogger<Poller>.Instance);

        await poller.PollOnceAsync(default);

        var queued = _kit.State.ListJobs(JobStatus.Pending).Select(job => job.Request.Number);
        Assert.Equal([7], queued);
    }

    [Fact]
    public void The_keyword_of_the_project_asks_for_the_next_round_and_the_old_one_does_not()
    {
        var issue = Samples.Issue(7, "bot");
        var analysis = Samples.Bot(101, Markers.Wrap("x", Markers.Analysis));
        var botMention = Samples.Human(102, "@bot please look again");
        var aiMention = Samples.Human(103, "@ai please look again");

        var withBot = JobPlanner.PlanIssue(new IssuePlanInput(_kit.Project, JobTestKit.Repo, issue, [analysis, botMention], 0));
        var withAi = JobPlanner.PlanIssue(new IssuePlanInput(_kit.Project, JobTestKit.Repo, issue, [analysis, aiMention], 0));

        Assert.Equal(JobType.AnalyzeIssue, withBot.Job?.Type);
        Assert.Null(withAi.Job);
        Assert.Contains("@bot", withAi.Reason);
    }

    [Fact]
    public void Labels_describe_the_keyword_of_the_project()
    {
        var labels = Labels.All("bot", "@bot");

        Assert.Equal("bot", labels[0].Name);
        Assert.Contains("@bot", labels.Single(label => label.Name == Labels.QuestionsOpen).Description);
    }

    [Theory]
    [InlineData("@bot", "hello @bot", true)]
    [InlineData("@bot", "@bot, please", true)]
    [InlineData("@bot", "mail me@bot.com", false)]
    [InlineData("@bot", "@bottle", false)]
    [InlineData("@bot", "@BOT", true)]
    [InlineData("!ai", "please !ai fix", true)]
    [InlineData("@a.i", "@axi", false)]
    public void Keyword_is_matched_as_a_word_of_its_own(string keyword, string text, bool expected)
    {
        var comment = Samples.Human(1, text);

        Assert.Equal(expected, BotComments.MentionsAi(comment, keyword));
    }
}

/// <summary>The .git/config guard covers every clone of the project, not only the one that is written.</summary>
public sealed class SiblingCloneGuardTests : IDisposable
{
    private readonly JobTestKit _kit = new(project: TestProjects.SeparateIssuesRepo());

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task A_changed_git_config_in_a_sibling_clone_discards_that_clone_and_pushes_nothing()
    {
        // Three clones (issues, backend, frontend) are read before and after the agent; only the frontend changes.
        string[] fingerprints = ["issues-1", "backend-1", "frontend-1", "issues-1", "backend-1", "frontend-CHANGED"];
        foreach (var fingerprint in fingerprints)
        {
            _kit.Git.Fingerprints.Enqueue(fingerprint);
        }

        var project = _kit.Project;
        var target = project.CodeComponents.First();
        var workspaceRequest = new WorkspaceRequest(project, target.Repo, "origin/main", "ai/issue-1", [])
        {
            Writable = true,
        };
        var prepared = await _kit.Workspace.PrepareAsync(workspaceRequest, default);
        var task = new AgentTask(
            "implement-issue",
            Workflows.Implement,
            "implement",
            "prompt",
            "fix prompt",
            "PR_BODY.md",
            _ => [],
            "origin/main");
        var context = new AgentRunContext(project, target, prepared, _kit.Run);

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => _kit.Session.RunAsync(task, context, default));

        Assert.Contains(".git/config of org/mbh-frontend", exception.Message);
        Assert.Equal(["org/mbh-frontend"], _kit.Workspace.Discarded);
        Assert.Empty(_kit.Git.Pushes);
    }
}
