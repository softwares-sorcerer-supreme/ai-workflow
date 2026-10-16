using Microsoft.Extensions.Logging.Abstractions;
using AiRunner.Tests.Fakes;
using AiRunner.Jobs;
using AiRunner.Infrastructure;

namespace AiRunner.Tests.Infrastructure;

/// <summary>
/// Real git and real shell, against a local bare repo: baseUrl "file:///..." makes
/// GitClient.RemoteUrl point at {root}/git/{owner}/{name}.git instead of GitBucket.
/// </summary>
public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ai-runner-git-").FullName;
    private readonly ProcessRunner _processRunner = new();
    private readonly RunnerConfig _config;
    private readonly GitClient _gitClient;
    private readonly Workspace _workspace;
    private readonly ProjectConfig _project;

    public GitWorkspaceTests()
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
        var templateFile = Path.Combine(_root, "templates", "analysis.md");
        File.WriteAllText(templateFile, "template");

        _gitClient = new GitClient(_config, new RunnerIdentity { Login = "bot", GitSecret = "x" }, _processRunner);
        _workspace = new Workspace(_config, _gitClient, NullLogger<Workspace>.Instance);

        // origin: a bare repo with one commit on main
        var bare = Path.Combine(_root, "git", "o", "r.git");
        Git(_root, "init", "--bare", "-b", "main", bare);

        var seed = Path.Combine(_root, "seed");
        Git(_root, "clone", bare, seed);
        var seedFile = Path.Combine(seed, "app.txt");
        File.WriteAllText(seedFile, "v1\n");
        Git(seed, "add", "-A");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        Git(seed, "push", "origin", "HEAD:refs/heads/main");
    }

    public void Dispose() => FileSystem.DeleteDirectory(_root);

    /// <summary>Prepares the project and returns the clone of o/r.</summary>
    private async Task<string> PrepareAsync(string startPoint, string? newBranch, params string[] extraFetch)
    {
        var request = new WorkspaceRequest(_project, "o/r", startPoint, newBranch, extraFetch);
        var prepared = await _workspace.PrepareAsync(request, default);

        return prepared.TargetClone;
    }

    private async Task<PreparedWorkspace> PrepareProjectAsync(ProjectConfig project, string targetRepo)
    {
        var request = new WorkspaceRequest(project, targetRepo, "origin/main", null, [])
        {
            Writable = true,
        };

        return await _workspace.PrepareAsync(request, default);
    }

    /// <summary>A second bare repo with one commit on main, like o/r.</summary>
    private void SeedRepo(string repo, params (string Path, string Content)[] files)
    {
        var bare = Path.Combine(_root, "git", repo.Replace('/', Path.DirectorySeparatorChar) + ".git");
        Git(_root, "init", "--bare", "-b", "main", bare);

        var seed = Path.Combine(_root, "seed-" + repo.Replace('/', '-'));
        Git(_root, "clone", bare, seed);
        foreach (var (path, content) in files)
        {
            var file = Path.Combine(seed, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }

        Git(seed, "add", "-A");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        Git(seed, "push", "origin", "HEAD:refs/heads/main");
    }

    [Fact]
    public async Task Implement_flow_branch_stage_commit_push_and_diff()
    {
        var directory = await PrepareAsync("origin/main", "ai/issue-1");

        var projectRoot = Path.GetDirectoryName(directory)!;
        var installedTemplate = Path.Combine(projectRoot, ".ai-workflow", "templates", "analysis.md");
        Assert.True(File.Exists(installedTemplate));
        var planFile = Path.Combine(directory, "plan.md");
        File.WriteAllText(planFile, "runner file, must not be committed");
        var prBodyFile = Path.Combine(directory, "PR_BODY.md");
        File.WriteAllText(prBodyFile, "runner file");
        var appFile = Path.Combine(directory, "app.txt");
        File.WriteAllText(appFile, "v2\nmore\n");
        var newFile = Path.Combine(directory, "new.txt");
        File.WriteAllText(newFile, "new\n");

        var stats = await _gitClient.StageAllAsync(directory, default);
        Assert.Equal(["app.txt", "new.txt"], stats.Select(stat => stat.Path).Order());
        Assert.Equal(4, stats.Sum(stat => stat.Lines));

        await _gitClient.CommitAsync(directory, "#1: test (Copilot)", default);
        await _gitClient.PushAsync(directory, "HEAD", "ai/issue-1", default);
        var head = await _gitClient.ResolveAsync(directory, "HEAD", default);
        Assert.Matches("^[0-9a-f]{40}$", head);

        // a fresh prepare sees the pushed branch and its commit count
        var again = await PrepareAsync("origin/main", null);
        Assert.True(await _gitClient.RefExistsAsync(again, "origin/ai/issue-1", default));
        Assert.Equal(head, await _gitClient.ResolveAsync(again, "origin/ai/issue-1", default));
        Assert.Null(await _gitClient.ResolveAsync(again, "origin/no-such-branch", default));
        Assert.Equal(1, await _gitClient.CountCommitsAsync(again, "origin/main", "origin/ai/issue-1", default));
        Assert.Equal(
            [_config.Git.UserEmail],
            await _gitClient.AuthorsAsync(again, "origin/main", "origin/ai/issue-1", default));
        Assert.Equal(
            ["app.txt", "new.txt"],
            (await _gitClient.ChangedFilesAsync(again, "origin/main", "origin/ai/issue-1", default)).Order());
        Assert.Contains("+more", await _gitClient.DiffAsync(again, "origin/main", "origin/ai/issue-1", default));
        var againPlanFile = Path.Combine(again, "plan.md");
        Assert.False(File.Exists(againPlanFile));   // cleaned
    }

    [Fact]
    public async Task Working_diff_shows_uncommitted_changes_but_not_runner_files()
    {
        var directory = await PrepareAsync("origin/main", "ai/issue-2");
        var appFile = Path.Combine(directory, "app.txt");
        File.WriteAllText(appFile, "v2\n");
        var fixFile = Path.Combine(directory, "fix.md");
        File.WriteAllText(fixFile, "runner file");
        var diffFile = Path.Combine(directory, "diff.patch");
        File.WriteAllText(diffFile, "runner file");

        var diff = await _gitClient.StageAndDiffAsync(directory, "origin/main", default);

        Assert.Contains("+v2", diff);
        Assert.DoesNotContain("fix.md", diff);
        Assert.DoesNotContain("diff.patch", diff);
    }

    [Fact]
    public async Task Feature_branch_is_pushed_from_a_remote_ref_and_missing_refs_fail_clearly()
    {
        var directory = await PrepareAsync("origin/main", null, "+refs/pull/9/head:refs/remotes/pr/9");
        await _gitClient.PushAsync(directory, "origin/main", "feat/issue-5", default);
        await _gitClient.CloneOrFetchAsync("o/r", directory, default);
        Assert.True(await _gitClient.RefExistsAsync(directory, "origin/feat/issue-5", default));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareAsync("origin/does-not-exist", null));
    }

    [Fact]
    public async Task Hooks_written_into_the_clone_never_run()
    {
        var directory = await PrepareAsync("origin/main", "ai/issue-2");
        var marker = Path.Combine(_root, "hook-ran");
        var hook = Path.Combine(directory, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, $"#!/bin/sh\necho ran > '{marker.Replace('\\', '/')}'\n");
        var appFile = Path.Combine(directory, "app.txt");
        File.WriteAllText(appFile, "changed\n");

        await _gitClient.StageAllAsync(directory, default);
        var before = _gitClient.ConfigFingerprint(directory);
        await _gitClient.CommitAsync(directory, "test", default);

        Assert.False(File.Exists(marker));
        Assert.Equal(before, _gitClient.ConfigFingerprint(directory));

        // Control: plain git (without the runner's settings) does run the hook on this machine.
        Git(directory, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "--allow-empty", "-m", "control");
        Assert.True(File.Exists(marker));
        var gitConfigFile = Path.Combine(directory, ".git", "config");
        Assert.DoesNotContain(
            "extraHeader",
            File.ReadAllText(gitConfigFile),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Every_repo_of_the_project_is_cloned_once_below_the_project_folder()
    {
        SeedRepo("o/s", ("lib.txt", "lib\n"));
        var project = new ProjectConfig
        {
            Name = "duo",
            IssuesIn = "r",
            Components =
            [
                new ComponentConfig { Name = "r", Repo = "o/r" },
                new ComponentConfig { Name = "s", Repo = "o/s" },
            ],
        };

        var prepared = await PrepareProjectAsync(project, "o/s");

        var workFolder = _config.ResolvePath(_config.Paths.Work);
        Assert.Equal(Path.Combine(workFolder, "duo"), prepared.Root);
        Assert.Equal(Path.Combine(prepared.Root, "r"), prepared.CloneOf("o/r"));
        Assert.Equal(Path.Combine(prepared.Root, "s"), prepared.TargetClone);
        Assert.True(File.Exists(Path.Combine(prepared.CloneOf("o/r"), "app.txt")));
        Assert.True(File.Exists(Path.Combine(prepared.CloneOf("o/s"), "lib.txt")));

        // The target is on its start point, the sibling is detached at its default branch.
        var siblingBranch = await GitOutputAsync(prepared.CloneOf("o/r"), "branch", "--show-current");
        Assert.Equal("", siblingBranch.Trim());
    }

    [Fact]
    public async Task Components_in_one_repo_share_one_clone()
    {
        SeedRepo("o/mono", ("apps/web/index.txt", "web\n"), ("apps/api/main.txt", "api\n"));
        var project = new ProjectConfig
        {
            Name = "mono",
            IssuesIn = "web",
            Components =
            [
                new ComponentConfig { Name = "web", Repo = "o/mono", Path = "apps/web" },
                new ComponentConfig { Name = "api", Repo = "o/mono", Path = "apps/api" },
            ],
        };

        var prepared = await PrepareProjectAsync(project, "o/mono");

        Assert.Single(prepared.Clones);
        var clones = Directory.GetDirectories(prepared.Root).Where(folder => !folder.EndsWith(".ai-workflow"));
        Assert.Equal([Path.Combine(prepared.Root, "mono")], clones);
    }

    [Fact]
    public async Task An_existing_clone_is_fetched_not_cloned_again()
    {
        var first = await PrepareAsync("origin/main", null);
        var keepFile = Path.Combine(first, ".git", "kept-between-jobs");
        File.WriteAllText(keepFile, "x");

        // A new commit on the server must be visible after the second prepare.
        var seed = Path.Combine(_root, "seed");
        File.WriteAllText(Path.Combine(seed, "app.txt"), "v2\n");
        Git(seed, "add", "-A");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "second");
        Git(seed, "push", "origin", "HEAD:refs/heads/main");

        var second = await PrepareAsync("origin/main", null);

        Assert.True(File.Exists(keepFile));
        Assert.Equal("v2\n", File.ReadAllText(Path.Combine(second, "app.txt")));
    }

    [Fact]
    public async Task Runner_files_of_the_last_job_are_deleted_from_the_project_folder()
    {
        var clone = await PrepareAsync("origin/main", null);
        var root = Path.GetDirectoryName(clone)!;
        string[] stale = ["context.md", "plan.md", "PR_BODY.md", "fix.md", "diff.patch", "changed-files.txt"];
        foreach (var name in stale)
        {
            File.WriteAllText(Path.Combine(root, name), "old job");
        }

        var attachments = Path.Combine(root, ".ai-workflow", "attachments");
        Directory.CreateDirectory(attachments);
        File.WriteAllText(Path.Combine(attachments, "old.png"), "old job");

        await PrepareAsync("origin/main", null);

        foreach (var name in stale)
        {
            Assert.False(File.Exists(Path.Combine(root, name)), name);
        }

        Assert.False(File.Exists(Path.Combine(attachments, "old.png")));
        Assert.True(File.Exists(Path.Combine(root, ".ai-workflow", "templates", "analysis.md")));
    }

    [Fact]
    public async Task Runner_files_stay_out_of_the_clone_and_out_of_a_commit()
    {
        var clone = await PrepareAsync("origin/main", "ai/issue-3");
        var root = Path.GetDirectoryName(clone)!;

        Assert.True(File.Exists(Path.Combine(root, "components.md")));
        Assert.False(File.Exists(Path.Combine(clone, "components.md")));

        // Even a runner file the agent writes inside the clone is never staged.
        File.WriteAllText(Path.Combine(clone, "components.md"), "agent copy");
        File.WriteAllText(Path.Combine(clone, "real.txt"), "work\n");
        var stats = await _gitClient.StageAllAsync(clone, default);

        Assert.Equal(["real.txt"], stats.Select(stat => stat.Path));
    }

    private static ProjectConfig DuoProject() =>
        new()
        {
            Name = "duo",
            IssuesIn = "r",
            Components =
            [
                new ComponentConfig { Name = "r", Repo = "o/r" },
                new ComponentConfig { Name = "s", Repo = "o/s" },
            ],
        };

    /// <summary>Pushes feat/issue-12 with one extra file to the seeded repo o/s.</summary>
    private void PushFeatureBranch()
    {
        var seed = Path.Combine(_root, "seed-o-s");
        Git(seed, "checkout", "-b", "feat/issue-12");
        File.WriteAllText(Path.Combine(seed, "feature.txt"), "from the feature branch\n");
        Git(seed, "add", "-A");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "feature");
        Git(seed, "push", "origin", "HEAD:refs/heads/feat/issue-12");
    }

    [Fact]
    public async Task The_other_repos_are_checked_out_at_the_feature_branch_when_it_exists()
    {
        SeedRepo("o/s", ("lib.txt", "lib\n"));
        PushFeatureBranch();
        var request = new WorkspaceRequest(DuoProject(), "o/r", "origin/main", null, [])
        {
            Writable = true,
            OthersAt = "feat/issue-12",
        };

        var prepared = await _workspace.PrepareAsync(request, default);

        var other = prepared.CloneOf("o/s");
        Assert.True(File.Exists(Path.Combine(other, "feature.txt")));
        Assert.Equal("", (await GitOutputAsync(other, "branch", "--show-current")).Trim());   // detached
        Assert.Equal("", (await GitOutputAsync(other, "status", "--porcelain")).Trim());      // clean
        var components = File.ReadAllText(Path.Combine(prepared.Root, "components.md"));
        Assert.Contains(
            "Checked out at: o/s at origin/feat/issue-12 (the feature branch of the parent issue)",
            components);
        Assert.Contains("Checked out at: o/r at origin/main (the start point of this job)", components);
    }

    [Fact]
    public async Task The_other_repos_stay_at_the_default_branch_when_the_feature_branch_is_missing()
    {
        SeedRepo("o/s", ("lib.txt", "lib\n"));
        var request = new WorkspaceRequest(DuoProject(), "o/r", "origin/main", null, [])
        {
            Writable = true,
            OthersAt = "feat/issue-12",
        };

        var prepared = await _workspace.PrepareAsync(request, default);

        var other = prepared.CloneOf("o/s");
        Assert.True(File.Exists(Path.Combine(other, "lib.txt")));
        Assert.False(File.Exists(Path.Combine(other, "feature.txt")));
        Assert.Equal("", (await GitOutputAsync(other, "status", "--porcelain")).Trim());
        var components = File.ReadAllText(Path.Combine(prepared.Root, "components.md"));
        Assert.Contains("Checked out at: o/s at origin/main (default branch)", components);
    }

    [Fact]
    public async Task Without_others_at_the_feature_branch_is_ignored_even_when_it_exists()
    {
        SeedRepo("o/s", ("lib.txt", "lib\n"));
        PushFeatureBranch();

        var prepared = await PrepareProjectAsync(DuoProject(), "o/r");

        Assert.False(File.Exists(Path.Combine(prepared.CloneOf("o/s"), "feature.txt")));
    }

    private static ProjectConfig AppAndDocsProject(params string[] appInstructions) =>
        new()
        {
            Name = "app",
            IssuesIn = "docs",
            Components =
            [
                new ComponentConfig { Name = "docs", Repo = "o/docs", Code = false },
                new ComponentConfig { Name = "app", Repo = "o/app", Instructions = [.. appInstructions] },
            ],
        };

    [Fact]
    public async Task Agents_md_in_the_project_folder_holds_the_description_and_the_instruction_files()
    {
        SeedRepo("o/docs", ("PROJECT.md", "about the project\n"));
        SeedRepo("o/app", ("AGENTS.md", "app rules\n"), ("docs/design.md", "design rules\n"));
        var project = AppAndDocsProject("AGENTS.md", "docs/design.md");

        var prepared = await PrepareProjectAsync(project, "o/app");

        var text = File.ReadAllText(Path.Combine(prepared.Root, "AGENTS.md"));
        Assert.Contains("about the project", text);
        Assert.Contains("## app: AGENTS.md (target - you may write here)", text);
        Assert.Contains("app rules", text);
        Assert.Contains("design rules", text);
        Assert.Contains($"Missing: {Path.Combine(prepared.Root, "docs", "AGENTS.md")} does not exist.", text);
    }

    [Fact]
    public async Task Agents_md_of_the_last_job_is_deleted_before_the_next_prepare()
    {
        SeedRepo("o/docs", ("PROJECT.md", "about\n"));
        SeedRepo("o/app", ("AGENTS.md", "app rules\n"));
        var project = AppAndDocsProject("AGENTS.md");
        var first = await PrepareProjectAsync(project, "o/app");
        File.WriteAllText(Path.Combine(first.Root, "AGENTS.md"), "left by the last job");

        var second = await PrepareProjectAsync(project, "o/app");

        var text = File.ReadAllText(Path.Combine(second.Root, "AGENTS.md"));
        Assert.DoesNotContain("left by the last job", text);
        Assert.Contains("app rules", text);
    }

    [Fact]
    public async Task Agents_md_is_not_in_the_exclude_list_of_a_clone_so_edits_to_a_repo_agents_md_are_committed()
    {
        SeedRepo("o/docs", ("PROJECT.md", "about\n"));
        SeedRepo("o/app", ("AGENTS.md", "app rules\n"));

        var prepared = await PrepareProjectAsync(AppAndDocsProject("AGENTS.md"), "o/app");

        var excludeFile = Path.Combine(prepared.CloneOf("o/app"), ".git", "info", "exclude");
        var lines = File.ReadAllLines(excludeFile);
        Assert.DoesNotContain("AGENTS.md", lines);
        Assert.Contains("components.md", lines);

        File.WriteAllText(Path.Combine(prepared.CloneOf("o/app"), "AGENTS.md"), "app rules, edited\n");
        var stats = await _gitClient.StageAllAsync(prepared.CloneOf("o/app"), default);

        Assert.Equal(["AGENTS.md"], stats.Select(stat => stat.Path));
    }

    [Fact]
    public async Task An_instruction_path_that_leaves_the_clone_is_treated_as_missing()
    {
        SeedRepo("o/docs", ("PROJECT.md", "about\n"));
        SeedRepo("o/app", ("AGENTS.md", "app rules\n"));
        var project = AppAndDocsProject("../secret.md");
        var rootSecret = Path.Combine(_config.ResolvePath(_config.Paths.Work), "app", "secret.md");
        Directory.CreateDirectory(Path.GetDirectoryName(rootSecret)!);
        File.WriteAllText(rootSecret, "do not copy me");

        var prepared = await PrepareProjectAsync(project, "o/app");

        var text = File.ReadAllText(Path.Combine(prepared.Root, "AGENTS.md"));
        Assert.DoesNotContain("do not copy me", text);
        Assert.Contains("Missing:", text);
    }

    [Fact]
    public async Task Components_md_lists_every_component_with_its_folder_and_role()
    {
        SeedRepo("o/docs", ("PROJECT.md", "about\n"));
        SeedRepo("o/app", ("AGENTS.md", "app rules\n"), ("sub/AGENTS.md", "sub rules\n"));
        var project = new ProjectConfig
        {
            Name = "app",
            IssuesIn = "docs",
            Components =
            [
                new ComponentConfig { Name = "docs", Repo = "o/docs", Code = false },
                new ComponentConfig { Name = "app", Repo = "o/app", Path = "." },
            ],
        };

        var prepared = await PrepareProjectAsync(project, "o/app");

        var componentsFile = Path.Combine(prepared.Root, "components.md");
        var text = File.ReadAllText(componentsFile);
        var docsLine = text.Split('\n').Single(line => line.StartsWith("| docs "));
        var appLine = text.Split('\n').Single(line => line.StartsWith("| app "));

        Assert.Contains("Project description (read it first)", text);
        Assert.Contains(Path.Combine(prepared.Root, "docs", "PROJECT.md"), text);
        Assert.Contains("| no |", docsLine);
        Assert.Contains("read-only", docsLine);
        Assert.Contains("target - you may write here", appLine);
        Assert.Contains(Path.Combine(prepared.Root, "app", "AGENTS.md"), appLine);
    }

    [Fact]
    public async Task Discard_deletes_only_that_clone()
    {
        SeedRepo("o/s", ("lib.txt", "lib\n"));
        var project = new ProjectConfig
        {
            Name = "duo",
            IssuesIn = "r",
            Components =
            [
                new ComponentConfig { Name = "r", Repo = "o/r" },
                new ComponentConfig { Name = "s", Repo = "o/s" },
            ],
        };
        var prepared = await PrepareProjectAsync(project, "o/r");

        _workspace.Discard(project, "o/s");

        Assert.False(Directory.Exists(prepared.CloneOf("o/s")));
        Assert.True(Directory.Exists(prepared.CloneOf("o/r")));
    }

    // ---- scope guard: the git side ----

    private async Task<string> PrepareSingleRepoAsync(string repo, params (string Path, string Content)[] files)
    {
        SeedRepo(repo, files);
        var project = new ProjectConfig
        {
            Name = "solo",
            IssuesIn = "g",
            Components = [new ComponentConfig { Name = "g", Repo = repo }],
        };
        var prepared = await PrepareProjectAsync(project, repo);

        return prepared.TargetClone;
    }

    private static void Write(string clone, string path, string content)
    {
        var file = Path.Combine(clone, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    private static string Read(string clone, string path) =>
        File.ReadAllText(Path.Combine(clone, path.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Exists(string clone, string path) =>
        File.Exists(Path.Combine(clone, path.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task Changed_paths_list_tracked_untracked_and_deleted_files_but_not_ignored_ones()
    {
        var clone = await PrepareSingleRepoAsync(
            "o/g", (".gitignore", "bin/\n"), ("keep.txt", "k\n"), ("gone.txt", "g\n"), ("sub/a.txt", "a\n"));
        Write(clone, "keep.txt", "changed\n");
        Write(clone, "staged.txt", "staged\n");
        Write(clone, "new file [1].txt", "new\n");
        Write(clone, "sub/n.txt", "n\n");
        Write(clone, "bin/out.dll", "ignored\n");
        File.Delete(Path.Combine(clone, "gone.txt"));
        Git(clone, "add", "staged.txt");

        var paths = await _gitClient.ChangedPathsAsync(clone, default);

        Assert.Equal(
            ["gone.txt", "keep.txt", "new file [1].txt", "staged.txt", "sub/n.txt"],
            paths.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Restore_puts_tracked_files_back_and_deletes_untracked_ones()
    {
        var clone = await PrepareSingleRepoAsync(
            "o/g", (".gitignore", "bin/\n"), ("keep.txt", "k\n"), ("gone.txt", "g\n"), ("sub/a.txt", "a\n"));
        Write(clone, "keep.txt", "changed\n");
        Write(clone, "sub/a.txt", "changed\n");
        Write(clone, "new file [1].txt", "new\n");
        Write(clone, "sub/n.txt", "n\n");
        File.Delete(Path.Combine(clone, "gone.txt"));
        Git(clone, "add", "-A");   // a staged change must go too

        string[] toUndo = ["keep.txt", "gone.txt", "new file [1].txt", "sub/n.txt"];
        await _gitClient.RestorePathsAsync(clone, toUndo, default);

        Assert.Equal("k\n", Read(clone, "keep.txt"));
        Assert.Equal("g\n", Read(clone, "gone.txt"));
        Assert.False(Exists(clone, "new file [1].txt"));
        Assert.False(Exists(clone, "sub/n.txt"));
        Assert.Equal("changed\n", Read(clone, "sub/a.txt"));   // not asked for, so it stays
        Assert.Equal(["sub/a.txt"], await _gitClient.ChangedPathsAsync(clone, default));
    }

    [Fact]
    public async Task Restore_never_deletes_outside_the_clone()
    {
        var clone = await PrepareSingleRepoAsync("o/g", ("keep.txt", "k\n"));
        var outside = Path.Combine(Path.GetDirectoryName(clone)!, "outside.txt");
        File.WriteAllText(outside, "mine");

        await _gitClient.RestorePathsAsync(clone, ["../outside.txt"], default);

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task The_scope_guard_keeps_the_target_component_and_undoes_everything_else()
    {
        SeedRepo(
            "o/mono",
            ("README.md", "readme\n"),
            ("apps/web/w.txt", "w\n"),
            ("apps/api/a.txt", "a\n"),
            ("apps/api/migrations/001.sql", "create\n"));
        SeedRepo("o/s", ("lib.txt", "lib\n"));
        var project = new ProjectConfig
        {
            Name = "mono",
            IssuesIn = "web",
            Components =
            [
                new ComponentConfig { Name = "web", Repo = "o/mono", Path = "apps/web" },
                new ComponentConfig { Name = "api", Repo = "o/mono", Path = "apps/api" },
                new ComponentConfig { Name = "sib", Repo = "o/s" },
            ],
        };
        project.Settings.NeverEdit = ["migrations/"];
        var request = new WorkspaceRequest(project, "o/mono", "origin/main", "ai/issue-1", [])
        {
            Writable = true,
            TargetComponent = "api",
        };
        var prepared = await _workspace.PrepareAsync(request, default);
        var mono = prepared.CloneOf("o/mono");
        var sibling = prepared.CloneOf("o/s");

        var copilot = new FakeCopilot();
        copilot.OnRun = (_, _) =>
        {
            Write(mono, "apps/api/a.txt", "a changed\n");
            Write(mono, "apps/api/new.txt", "new\n");
            Write(mono, "apps/web/w.txt", "w changed\n");
            Write(mono, "README.md", "readme changed\n");
            Write(mono, "apps/api/migrations/001.sql", "drop\n");
            Write(sibling, "lib.txt", "lib changed\n");
            Write(sibling, "extra.txt", "extra\n");
        };
        var session = new AgentSession(_config, _workspace, _gitClient, copilot, new FakeProcesses());
        var task = new AgentTask(
            "implement-issue", Workflows.Implement, "implement", "prompt", "fix prompt", "PR_BODY.md", _ => [], "origin/main");
        var runFolder = Path.Combine(_root, "run");
        var run = new JobRun(runFolder, NullLogger.Instance);
        var context = new AgentRunContext(project, project.Components[1], prepared, run);

        var result = await session.RunAsync(task, context, default);

        // Kept: the edits inside the target component. Undone: all other edits, listed with a reason.
        Assert.Equal("a changed\n", Read(mono, "apps/api/a.txt"));
        Assert.Equal("new\n", Read(mono, "apps/api/new.txt"));
        Assert.Equal("w\n", Read(mono, "apps/web/w.txt"));
        Assert.Equal("readme\n", Read(mono, "README.md"));
        Assert.Equal("create\n", Read(mono, "apps/api/migrations/001.sql"));
        Assert.Equal("lib\n", Read(sibling, "lib.txt"));
        Assert.False(Exists(sibling, "extra.txt"));

        Assert.Equal(
            [
                ("o/mono", "README.md", ScopeRules.OutsideReason),
                ("o/mono", "apps/api/migrations/001.sql", ScopeRules.NeverEditReason),
                ("o/mono", "apps/web/w.txt", ScopeRules.OutsideReason),
                ("o/s", "extra.txt", ScopeRules.ReadOnlyReason),
                ("o/s", "lib.txt", ScopeRules.ReadOnlyReason),
            ],
            result.Undone
                .Select(change => (change.Repo, change.Path, change.Reason))
                .OrderBy(change => change.Repo, StringComparer.Ordinal)
                .ThenBy(change => change.Path, StringComparer.Ordinal));

        // What is committed is the target component only.
        var stats = await _gitClient.StageAllAsync(mono, default);
        Assert.Equal(["apps/api/a.txt", "apps/api/new.txt"], stats.Select(stat => stat.Path).Order());
    }

    private async Task<string> GitOutputAsync(string directory, params string[] args)
    {
        var timeout = TimeSpan.FromMinutes(1);
        var result = await _processRunner.RunAsync(new ProcessSpec("git", args, directory, timeout), default);

        return result.StdOut;
    }

    [Fact]
    public async Task Shell_runs_a_repos_yml_command_line()
    {
        var command = OperatingSystem.IsWindows() ? "echo hello && exit 3" : "echo hello; exit 3";
        var timeout = TimeSpan.FromSeconds(30);
        var spec = ProcessSpec.Shell(command, _root, timeout);
        var result = await _processRunner.RunAsync(spec, default);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("hello", result.StdOut);
    }

    private void Git(string directory, params string[] args)
    {
        var timeout = TimeSpan.FromMinutes(1);
        var result = _processRunner
            .RunAsync(new ProcessSpec("git", args, directory, timeout), default)
            .GetAwaiter()
            .GetResult();
        if (!result.Ok)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {result.Tail()}");
        }
    }
}
