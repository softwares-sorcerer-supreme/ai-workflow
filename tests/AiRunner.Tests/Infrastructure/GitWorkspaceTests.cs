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

    public GitWorkspaceTests()
    {
        var reposFile = Path.Combine(_root, "repos.yml");
        File.WriteAllText(reposFile, $"""
            gitBucket:
              baseUrl: {new Uri(_root).AbsoluteUri}
            """);
        _config = RunnerConfig.Load(reposFile);
        var templatesFolder = Path.Combine(_root, "templates");
        Directory.CreateDirectory(templatesFolder);
        var templateFile = Path.Combine(_root, "templates", "analysis.md");
        File.WriteAllText(templateFile, "template");

        _gitClient = new GitClient(_config, new RunnerIdentity { Login = "bot", GitSecret = "x" }, _processRunner);
        _workspace = new Workspace(_config, _gitClient);

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

    private Task<string> PrepareAsync(string startPoint, string? newBranch, params string[] extraFetch)
    {
        var request = new WorkspaceRequest("o/r", startPoint, newBranch, extraFetch);

        return _workspace.PrepareAsync(request, default);
    }

    [Fact]
    public async Task Implement_flow_branch_stage_commit_push_and_diff()
    {
        var directory = await PrepareAsync("origin/main", "ai/issue-1");

        var installedTemplate = Path.Combine(directory, ".ai-workflow", "templates", "analysis.md");
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
