using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Infrastructure;

/// <summary>
/// What the Workspaces screen reads and does, with real git against a local bare repo (like GitWorkspaceTests):
/// the clone info and warnings, fetch, re-clone, the project lock and the orphan delete.
/// </summary>
public sealed class WorkspaceActionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ai-runner-wsact-").FullName;
    private readonly ProcessRunner _processRunner = new();
    private readonly RunnerConfig _config;
    private readonly ProjectConfig _project;
    private readonly WorkspaceLocks _locks = new();
    private readonly DirectorySizes _sizes = new(TimeProvider.System);
    private readonly GitClient _gitClient;
    private readonly Workspace _workspace;
    private readonly RunnerWorkspaces _workspaces;
    private readonly string _seed;

    public WorkspaceActionsTests()
    {
        var reposFile = Path.Combine(_root, "repos.yml");
        File.WriteAllText(reposFile, $"""
            git_bucket:
              base_url: {new Uri(_root).AbsoluteUri}
            repos:
              - owner: o
                name: r
            """);
        _config = RunnerConfig.Load(reposFile);
        _project = _config.Projects.Single();

        var templatesFolder = Path.Combine(_root, "templates");
        Directory.CreateDirectory(templatesFolder);
        File.WriteAllText(Path.Combine(templatesFolder, "analysis.md"), "template");
        var agentsFolder = Path.Combine(_root, "agents");
        Directory.CreateDirectory(agentsFolder);
        File.WriteAllText(Path.Combine(agentsFolder, "implement-issue.agent.md"), "shared");

        var identity = new RunnerIdentity { Login = "bot" };
        var remote = new GitBucketGitRemote(
            RunnerConfigSource.Fixed(_config),
            identity,
            RunnerCredentials.Of(token: "x"));
        _gitClient = new GitClient(RunnerConfigSource.Fixed(_config), remote, _processRunner);
        _workspace = new Workspace(
            RunnerConfigSource.Fixed(_config),
            _gitClient,
            remote,
            _locks,
            NullLogger<Workspace>.Instance);
        var inspector = new WorkspaceInspector(RunnerConfigSource.Fixed(_config), _gitClient, _locks, _sizes);
        _workspaces = new RunnerWorkspaces(
            RunnerConfigSource.Fixed(_config),
            inspector,
            _gitClient,
            _workspace,
            _locks,
            NullLogger<RunnerWorkspaces>.Instance);

        var bare = Path.Combine(_root, "git", "o", "r.git");
        Git(_root, "init", "--bare", "-b", "main", bare);
        _seed = Path.Combine(_root, "seed");
        Git(_root, "clone", bare, _seed);
        Commit("AGENTS.md", "rules\n", "init");
    }

    public void Dispose() => FileSystem.DeleteDirectory(_root);

    private string CloneFolder => Path.Combine(_root, "work", _project.Name, "r");

    private string WorkFolder => Path.Combine(_root, "work");

    private void Commit(string path, string content, string message)
    {
        var file = Path.Combine(_seed, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        Git(_seed, "add", "-A");
        Git(_seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", message);
        Git(_seed, "push", "origin", "HEAD:refs/heads/main");
    }

    /// <summary>Runs a job's prepare and then the end of the job, like the worker does.</summary>
    private async Task PrepareAsync(string? newBranch = null)
    {
        var request = new WorkspaceRequest(_project, "o/r", "origin/main", newBranch, null);
        await _workspace.PrepareAsync(request, default);
        _locks.ReleaseJobHolds();
    }

    private async Task<CloneInfo> CloneInfoAsync()
    {
        var listing = await _workspaces.ListAsync(default);

        return listing.Projects.Single().Clones.Single();
    }

    [Fact]
    public async Task A_clone_that_is_not_there_yet_says_so_and_has_no_warnings()
    {
        var clone = await CloneInfoAsync();

        Assert.False(clone.IsOnDisk);
        Assert.Null(clone.Head);
        Assert.Empty(clone.Warnings);
        Assert.Equal(["r"], clone.Components.Select(_ => "r"));
    }

    [Fact]
    public async Task A_prepared_clone_shows_its_commit_detached_at_origin_and_its_last_fetch()
    {
        // The first prepare clones; only the second fetches, which is what writes FETCH_HEAD.
        await PrepareAsync();
        await PrepareAsync();

        var clone = await CloneInfoAsync();

        Assert.True(clone.IsOnDisk);
        Assert.NotNull(clone.Head);
        Assert.Equal("init", clone.Head.Subject);
        Assert.Equal(40, clone.Head.Sha.Length);
        Assert.Null(clone.Head.Branch);
        Assert.Equal("origin/main", clone.Head.DetachedAt);
        Assert.NotNull(clone.LastFetchAt);
        Assert.Empty(clone.ChangedPaths);
        Assert.Empty(clone.Warnings);
    }

    [Fact]
    public async Task A_clone_on_a_new_branch_shows_the_branch()
    {
        await PrepareAsync("ai/issue-1");

        var clone = await CloneInfoAsync();

        Assert.Equal("ai/issue-1", clone.Head!.Branch);
        Assert.Null(clone.Head.DetachedAt);
    }

    [Fact]
    public async Task A_repo_agent_with_the_name_of_a_shared_one_is_a_warning()
    {
        Commit(".github/agents/implement-issue.agent.md", "repo copy\n", "override");
        Commit(".github/agents/own.agent.md", "own\n", "own");
        await PrepareAsync();

        var clone = await CloneInfoAsync();

        var warning = Assert.Single(clone.Warnings);
        Assert.Equal(WorkspaceChecks.AgentOverride, warning.Code);
        Assert.Contains("implement-issue.agent.md", warning.Message);
    }

    [Fact]
    public async Task A_missing_instruction_file_is_a_warning()
    {
        _project.Components.Single().Instructions = ["AGENTS.md", "docs/RULES.md"];
        await PrepareAsync();

        var clone = await CloneInfoAsync();

        var warning = Assert.Single(clone.Warnings);
        Assert.Equal(WorkspaceChecks.InstructionsMissing, warning.Code);
        Assert.Contains("docs/RULES.md", warning.Message);
    }

    [Fact]
    public async Task An_instruction_path_that_leaves_the_clone_counts_as_missing()
    {
        _project.Components.Single().Instructions = ["../outside.md"];
        Directory.CreateDirectory(Path.Combine(_root, "work", _project.Name));
        File.WriteAllText(Path.Combine(_root, "work", _project.Name, "outside.md"), "x");
        await PrepareAsync();

        var clone = await CloneInfoAsync();

        Assert.Contains(clone.Warnings, warning => warning.Code == WorkspaceChecks.InstructionsMissing);
    }

    [Fact]
    public async Task Changes_left_in_a_clone_are_a_warning_but_not_while_a_job_holds_the_project()
    {
        await PrepareAsync();
        File.WriteAllText(Path.Combine(CloneFolder, "AGENTS.md"), "edited\n");
        File.WriteAllText(Path.Combine(CloneFolder, "new.txt"), "new\n");

        var idle = await CloneInfoAsync();

        Assert.Equal(["AGENTS.md", "new.txt"], idle.ChangedPaths.Order());
        Assert.Contains(idle.Warnings, warning => warning.Code == WorkspaceChecks.HasChanges);

        await _locks.HoldForJobAsync(_project.Name, default);
        var listing = await _workspaces.ListAsync(default);

        var busy = listing.Projects.Single();
        Assert.True(busy.IsBusy);
        Assert.Empty(busy.Clones.Single().ChangedPaths);
        Assert.Empty(busy.Clones.Single().Warnings);
    }

    [Fact]
    public async Task The_size_is_unknown_at_first_and_known_after_the_background_walk()
    {
        await PrepareAsync();

        var first = await CloneInfoAsync();
        await _sizes.WhenIdleAsync();
        var second = await CloneInfoAsync();

        Assert.Null(first.SizeBytes);
        Assert.True(second.SizeBytes > 0);
    }

    [Fact]
    public async Task Listing_never_needs_the_network()
    {
        await PrepareAsync();
        FileSystem.DeleteDirectory(Path.Combine(_root, "git"));

        var clone = await CloneInfoAsync();

        Assert.True(clone.IsOnDisk);
        Assert.NotNull(clone.Head);
    }

    [Fact]
    public async Task Fetch_brings_in_the_new_commits_of_the_remote()
    {
        await PrepareAsync();
        var before = await _gitClient.ResolveAsync(CloneFolder, "origin/main", default);
        Commit("a.txt", "a\n", "second");

        var reply = await _workspaces.FetchAsync(_project.Name, "r", default);

        Assert.Equal(WorkspaceOutcome.Done, reply.Outcome);
        var after = await _gitClient.ResolveAsync(CloneFolder, "origin/main", default);
        Assert.NotEqual(before, after);
        Assert.False(_locks.IsHeld(_project.Name));
    }

    [Fact]
    public async Task Fetch_of_a_clone_that_is_not_made_yet_is_refused()
    {
        var reply = await _workspaces.FetchAsync(_project.Name, "r", default);

        Assert.Equal(WorkspaceOutcome.Refused, reply.Outcome);
    }

    [Fact]
    public async Task Fetch_that_git_rejects_is_reported_as_failed_and_frees_the_lock()
    {
        await PrepareAsync();
        FileSystem.DeleteDirectory(Path.Combine(_root, "git"));

        var reply = await _workspaces.FetchAsync(_project.Name, "r", default);

        Assert.Equal(WorkspaceOutcome.Failed, reply.Outcome);
        Assert.False(_locks.IsHeld(_project.Name));
    }

    [Theory]
    [InlineData("nope", "r")]
    [InlineData("PROJECT", "nope")]
    [InlineData("PROJECT", "o/r")]
    [InlineData("PROJECT", "..")]
    public async Task An_unknown_project_or_clone_is_not_found(string project, string folder)
    {
        var name = project == "PROJECT" ? _project.Name : project;

        var fetch = await _workspaces.FetchAsync(name, folder, default);
        var reclone = await _workspaces.RecloneAsync(name, folder, default);

        Assert.Equal(WorkspaceOutcome.NotFound, fetch.Outcome);
        Assert.Equal(WorkspaceOutcome.NotFound, reclone.Outcome);
    }

    [Fact]
    public async Task Reclone_deletes_the_clone_and_the_next_prepare_makes_it_again()
    {
        await PrepareAsync();

        var reply = await _workspaces.RecloneAsync(_project.Name, "r", default);

        Assert.Equal(WorkspaceOutcome.Done, reply.Outcome);
        Assert.False(Directory.Exists(CloneFolder));

        await PrepareAsync();

        Assert.True(Directory.Exists(Path.Combine(CloneFolder, ".git")));
    }

    [Fact]
    public async Task A_job_holds_the_project_from_prepare_until_the_worker_releases_it()
    {
        var request = new WorkspaceRequest(_project, "o/r", "origin/main", null, null);

        await _workspace.PrepareAsync(request, default);
        await _workspace.PrepareAsync(request, default);

        Assert.True(_locks.IsHeld(_project.Name));
        Assert.Equal(WorkspaceOutcome.Busy, (await _workspaces.FetchAsync(_project.Name, "r", default)).Outcome);
        Assert.Equal(WorkspaceOutcome.Busy, (await _workspaces.RecloneAsync(_project.Name, "r", default)).Outcome);
        Assert.True(Directory.Exists(CloneFolder));

        _locks.ReleaseJobHolds();

        Assert.False(_locks.IsHeld(_project.Name));
        Assert.Equal(WorkspaceOutcome.Done, (await _workspaces.RecloneAsync(_project.Name, "r", default)).Outcome);
    }

    [Fact]
    public async Task A_job_waits_for_a_running_action_instead_of_failing()
    {
        var action = _locks.TryHoldForAction(_project.Name);
        Assert.NotNull(action);
        var request = new WorkspaceRequest(_project, "o/r", "origin/main", null, null);

        var prepare = _workspace.PrepareAsync(request, default);
        await Task.Delay(400);

        Assert.False(prepare.IsCompleted);

        action.Dispose();
        await prepare.WaitAsync(TimeSpan.FromSeconds(30));
        _locks.ReleaseJobHolds();
    }

    [Fact]
    public void Two_actions_do_not_hold_the_same_project_and_other_projects_stay_free()
    {
        using var first = _locks.TryHoldForAction("a");

        Assert.NotNull(first);
        Assert.Null(_locks.TryHoldForAction("A"));
        using var other = _locks.TryHoldForAction("b");
        Assert.NotNull(other);
    }

    [Fact]
    public async Task Orphans_are_the_folders_of_no_project_and_never_hidden_ones()
    {
        await PrepareAsync();
        Directory.CreateDirectory(Path.Combine(WorkFolder, "old-project"));
        File.WriteAllText(Path.Combine(WorkFolder, "old-project", "x.txt"), "x");
        Directory.CreateDirectory(Path.Combine(WorkFolder, ".no-hooks"));

        var listing = await _workspaces.ListAsync(default);

        Assert.Equal(["old-project"], listing.Orphans.Select(orphan => orphan.Folder));
    }

    [Fact]
    public async Task Delete_orphan_removes_a_plain_folder_with_read_only_files()
    {
        var folder = Path.Combine(WorkFolder, "old-project");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "x.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var reply = await _workspaces.DeleteOrphanAsync("old-project", default);

        Assert.Equal(WorkspaceOutcome.Done, reply.Outcome);
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../seed")]
    [InlineData("..\\seed")]
    [InlineData("sub/dir")]
    [InlineData("sub\\dir")]
    [InlineData("/etc")]
    [InlineData("C:\\Windows")]
    [InlineData("")]
    [InlineData(".no-hooks")]
    public async Task Delete_orphan_refuses_anything_but_a_plain_name_and_deletes_nothing(string folder)
    {
        Directory.CreateDirectory(Path.Combine(WorkFolder, "sub", "dir"));

        var reply = await _workspaces.DeleteOrphanAsync(folder, default);

        Assert.Equal(WorkspaceOutcome.Refused, reply.Outcome);
        Assert.True(Directory.Exists(_seed));
        Assert.True(Directory.Exists(Path.Combine(WorkFolder, "sub", "dir")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_orphan_refuses_a_project_folder_in_any_case(bool isUpperCase)
    {
        await PrepareAsync();
        var name = isUpperCase ? _project.Name.ToUpperInvariant() : _project.Name;

        var reply = await _workspaces.DeleteOrphanAsync(name, default);

        Assert.Equal(WorkspaceOutcome.Refused, reply.Outcome);
        Assert.True(Directory.Exists(CloneFolder));
    }

    [Fact]
    public async Task Delete_orphan_is_busy_while_the_same_folder_is_being_deleted()
    {
        var folder = Path.Combine(WorkFolder, "old-project");
        Directory.CreateDirectory(folder);
        using var other = _locks.TryHoldForAction("old-project");

        var reply = await _workspaces.DeleteOrphanAsync("old-project", default);

        Assert.Equal(WorkspaceOutcome.Busy, reply.Outcome);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task Delete_orphan_is_busy_while_a_job_of_a_removed_project_still_runs_in_it()
    {
        var folder = Path.Combine(WorkFolder, "old-project");
        Directory.CreateDirectory(folder);
        await _locks.HoldForJobAsync("old-project", default);

        var reply = await _workspaces.DeleteOrphanAsync("old-project", default);

        Assert.Equal(WorkspaceOutcome.Busy, reply.Outcome);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task Delete_orphan_of_a_folder_that_is_not_there_is_not_found()
    {
        var reply = await _workspaces.DeleteOrphanAsync("never-existed", default);

        Assert.Equal(WorkspaceOutcome.NotFound, reply.Outcome);
    }

    [Fact]
    public async Task Delete_orphan_refuses_a_link_and_keeps_its_target()
    {
        var target = Path.Combine(_root, "link-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        Directory.CreateDirectory(WorkFolder);
        var link = Path.Combine(WorkFolder, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // Creating a symbolic link needs a privilege on Windows; without it there is nothing to test here.
            return;
        }

        var reply = await _workspaces.DeleteOrphanAsync("linked", default);

        Assert.Equal(WorkspaceOutcome.Refused, reply.Outcome);
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    private void Git(string directory, params string[] args)
    {
        var result = _processRunner
            .RunAsync(new ProcessSpec("git", args, directory, TimeSpan.FromMinutes(1)), default)
            .GetAwaiter()
            .GetResult();
        if (!result.Ok)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {result.Tail()}");
        }
    }
}
