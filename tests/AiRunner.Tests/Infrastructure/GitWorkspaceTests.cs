using AiRunner.Infrastructure;

namespace AiRunner.Tests.Infrastructure;

/// <summary>
/// Real git and real shell, against a local bare repo: baseUrl "file:///..." makes
/// GitClient.RemoteUrl point at {root}/git/{owner}/{name}.git instead of GitBucket.
/// </summary>
public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ai-runner-git-").FullName;
    private readonly ProcessRunner _processes = new();
    private readonly RunnerConfig _config;
    private readonly GitClient _git;
    private readonly Workspace _workspace;

    public GitWorkspaceTests()
    {
        File.WriteAllText(Path.Combine(_root, "repos.yml"), $"""
            gitBucket:
              baseUrl: {new Uri(_root).AbsoluteUri}
            """);
        _config = RunnerConfig.Load(Path.Combine(_root, "repos.yml"));
        Directory.CreateDirectory(Path.Combine(_root, "templates"));
        File.WriteAllText(Path.Combine(_root, "templates", "analysis.md"), "template");

        _git = new GitClient(_config, new RunnerIdentity { Login = "bot", GitSecret = "x" }, _processes);
        _workspace = new Workspace(_config, _git);

        // origin: a bare repo with one commit on main
        var bare = Path.Combine(_root, "git", "o", "r.git");
        Git(_root, "init", "--bare", "-b", "main", bare);
        var seed = Path.Combine(_root, "seed");
        Git(_root, "clone", bare, seed);
        File.WriteAllText(Path.Combine(seed, "app.txt"), "v1\n");
        Git(seed, "add", "-A");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        Git(seed, "push", "origin", "HEAD:refs/heads/main");
    }

    public void Dispose() => FileSystem.DeleteDirectory(_root);

    [Fact]
    public async Task Implement_flow_branch_stage_commit_push_and_diff()
    {
        var dir = await _workspace.PrepareAsync("o/r", "origin/main", "ai/issue-1", [], default);

        Assert.True(File.Exists(Path.Combine(dir, ".ai-workflow", "templates", "analysis.md")));
        File.WriteAllText(Path.Combine(dir, "plan.md"), "runner file, must not be committed");
        File.WriteAllText(Path.Combine(dir, "PR_BODY.md"), "runner file");
        File.WriteAllText(Path.Combine(dir, "app.txt"), "v2\nmore\n");
        File.WriteAllText(Path.Combine(dir, "new.txt"), "new\n");

        var stats = await _git.StageAllAsync(dir, default);
        Assert.Equal(["app.txt", "new.txt"], stats.Select(s => s.Path).Order());
        Assert.Equal(4, stats.Sum(s => s.Lines));

        await _git.CommitAsync(dir, "#1: test (Copilot)", default);
        await _git.PushAsync(dir, "HEAD", "ai/issue-1", default);
        var head = await _git.ResolveAsync(dir, "HEAD", default);
        Assert.Matches("^[0-9a-f]{40}$", head);

        // a fresh prepare sees the pushed branch and its commit count
        var again = await _workspace.PrepareAsync("o/r", "origin/main", null, [], default);
        Assert.True(await _git.RefExistsAsync(again, "origin/ai/issue-1", default));
        Assert.Equal(head, await _git.ResolveAsync(again, "origin/ai/issue-1", default));
        Assert.Null(await _git.ResolveAsync(again, "origin/no-such-branch", default));
        Assert.Equal(1, await _git.CountCommitsAsync(again, "origin/main", "origin/ai/issue-1", default));
        Assert.Equal([_config.Git.UserEmail], await _git.AuthorsAsync(again, "origin/main", "origin/ai/issue-1", default));
        Assert.Equal(["app.txt", "new.txt"], (await _git.ChangedFilesAsync(again, "origin/main", "origin/ai/issue-1", default)).Order());
        Assert.Contains("+more", await _git.DiffAsync(again, "origin/main", "origin/ai/issue-1", default));
        Assert.False(File.Exists(Path.Combine(again, "plan.md")));   // cleaned
    }

    [Fact]
    public async Task Working_diff_shows_uncommitted_changes_but_not_runner_files()
    {
        var dir = await _workspace.PrepareAsync("o/r", "origin/main", "ai/issue-2", [], default);
        File.WriteAllText(Path.Combine(dir, "app.txt"), "v2\n");
        File.WriteAllText(Path.Combine(dir, "fix.md"), "runner file");
        File.WriteAllText(Path.Combine(dir, "diff.patch"), "runner file");

        var diff = await _git.StageAndDiffAsync(dir, "origin/main", default);

        Assert.Contains("+v2", diff);
        Assert.DoesNotContain("fix.md", diff);
        Assert.DoesNotContain("diff.patch", diff);
    }

    [Fact]
    public async Task Feature_branch_is_pushed_from_a_remote_ref_and_missing_refs_fail_clearly()
    {
        var dir = await _workspace.PrepareAsync("o/r", "origin/main", null, ["+refs/pull/9/head:refs/remotes/pr/9"], default);
        await _git.PushAsync(dir, "origin/main", "feat/issue-5", default);
        await _git.CloneOrFetchAsync("o/r", dir, default);
        Assert.True(await _git.RefExistsAsync(dir, "origin/feat/issue-5", default));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workspace.PrepareAsync("o/r", "origin/does-not-exist", null, [], default));
    }

    [Fact]
    public async Task Hooks_written_into_the_clone_never_run()
    {
        var dir = await _workspace.PrepareAsync("o/r", "origin/main", "ai/issue-2", [], default);
        var marker = Path.Combine(_root, "hook-ran");
        var hook = Path.Combine(dir, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, $"#!/bin/sh\necho ran > '{marker.Replace('\\', '/')}'\n");
        File.WriteAllText(Path.Combine(dir, "app.txt"), "changed\n");

        await _git.StageAllAsync(dir, default);
        var before = _git.ConfigFingerprint(dir);
        await _git.CommitAsync(dir, "test", default);

        Assert.False(File.Exists(marker));
        Assert.Equal(before, _git.ConfigFingerprint(dir));

        // Control: plain git (without the runner's settings) does run the hook on this machine.
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "--allow-empty", "-m", "control");
        Assert.True(File.Exists(marker));
        Assert.DoesNotContain("extraHeader", File.ReadAllText(Path.Combine(dir, ".git", "config")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shell_runs_a_repos_yml_command_line()
    {
        var command = OperatingSystem.IsWindows() ? "echo hello && exit 3" : "echo hello; exit 3";
        var r = await _processes.RunAsync(ProcessSpec.Shell(command, _root, TimeSpan.FromSeconds(30)), default);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("hello", r.StdOut);
    }

    private void Git(string dir, params string[] args)
    {
        var r = _processes.RunAsync(new ProcessSpec("git", args, dir, TimeSpan.FromMinutes(1)), default).GetAwaiter().GetResult();
        if (!r.Ok) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Tail()}");
    }
}
