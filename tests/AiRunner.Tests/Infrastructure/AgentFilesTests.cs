using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Infrastructure;

/// <summary>
/// The shared files against a temp "runner repo" (real git) and a temp Copilot home. The real ~/.copilot is never
/// touched: the home folder is a parameter everywhere.
/// </summary>
public sealed class AgentFilesTests : IDisposable
{
    private const string Agent = "---\nname: scout\ndescription: Finds code.\n---\n\nBody\n";
    private const string Skill = "---\nname: checklist\ndescription: Rules.\n---\n\nRules\n";

    private readonly string _root = Directory.CreateTempSubdirectory("ai-runner-agents-").FullName;
    private readonly string _home;
    private readonly ProcessRunner _processRunner = new();
    private readonly RunnerConfig _config;
    private readonly AgentFiles _files;
    private readonly RunnerCommands _commands = new();
    private readonly RunnerAgents _agents;
    private readonly AgentSync _sync;

    public AgentFilesTests()
    {
        _home = Path.Combine(_root, "copilot-home");
        var repo = Path.Combine(_root, "repo");
        _config = new RunnerConfig { RootDir = repo };
        Write("agents/scout.agent.md", Agent);
        Write("skills/checklist/SKILL.md", Skill);
        Write("templates/spec.md", "# Spec\n");

        Git(repo, "init", "-b", "main");
        Git(repo, "add", "-A");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "seed");

        var copilotHome = new CopilotHome(_home);
        _files = new AgentFiles(_config, copilotHome, _processRunner);
        var installer = new AgentInstaller(_config, NullLogger<AgentInstaller>.Instance);
        _sync = new AgentSync(installer, copilotHome, _commands, NullLogger<AgentSync>.Instance);
        _agents = new RunnerAgents(_files, _sync, NullLogger<RunnerAgents>.Instance);
    }

    public void Dispose() => FileSystem.DeleteDirectory(_root);

    private string Repo(string path) => Path.Combine(_root, "repo", path);

    private void Write(string path, string text)
    {
        var file = Repo(path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    /// <summary>The file's text, or null while it is missing or still being written by the copy.</summary>
    private static string? TextOrNull(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Git(string directory, params string[] args)
    {
        var result = _processRunner
            .RunAsync(new ProcessSpec("git", args, directory, TimeSpan.FromMinutes(1)), default)
            .GetAwaiter()
            .GetResult();
        Assert.True(result.Ok, $"git {string.Join(' ', args)}: {result.Tail()}");
    }

    private async Task<AgentFileDetail> ReadAsync(string path)
    {
        var detail = await _agents.ReadAsync(path, default);
        Assert.NotNull(detail);

        return detail;
    }

    // ---- listing and git state ----

    [Fact]
    public async Task The_list_has_agents_skills_and_read_only_templates_with_git_and_sync_states()
    {
        var listing = await _agents.ListAsync(default);

        Assert.Equal(
            ["agents/scout.agent.md", "skills/checklist/SKILL.md", "templates/spec.md"],
            listing.Files.Select(file => file.Path));
        var agent = listing.Files.Single(file => file.Kind == AgentKinds.Agent);
        var template = listing.Files.Single(file => file.Kind == AgentKinds.Template);
        Assert.Equal(AgentStates.Unchanged, agent.GitState);
        Assert.Equal(AgentStates.Missing, agent.SyncState);
        Assert.True(agent.IsEditable);
        Assert.False(template.IsEditable);
        Assert.Equal(AgentStates.NotInstalled, template.SyncState);
        Assert.True(listing.IsOutOfSync);
    }

    [Fact]
    public async Task Git_states_are_modified_untracked_and_unchanged()
    {
        Write("agents/scout.agent.md", Agent + "more\n");
        Write("skills/checklist/extra.md", "x");

        var listing = await _agents.ListAsync(default);

        Assert.Equal(AgentStates.Modified, listing.Files.Single(file => file.Path == "agents/scout.agent.md").GitState);
        Assert.Equal(AgentStates.Untracked, listing.Files.Single(file => file.Path == "skills/checklist/extra.md").GitState);
        Assert.Equal(AgentStates.Unchanged, listing.Files.Single(file => file.Path == "skills/checklist/SKILL.md").GitState);
    }

    [Fact]
    public async Task Outside_a_git_repo_every_state_is_unknown()
    {
        var plainRoot = Directory.CreateTempSubdirectory("ai-runner-nogit-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(plainRoot, "agents"));
            File.WriteAllText(Path.Combine(plainRoot, "agents", "scout.agent.md"), Agent);
            var files = new AgentFiles(new RunnerConfig { RootDir = plainRoot }, new CopilotHome(_home), _processRunner);

            var states = await files.GitStatesAsync(["agents/scout.agent.md"], default);

            Assert.Equal(AgentStates.Unknown, states["agents/scout.agent.md"]);
        }
        finally
        {
            FileSystem.DeleteDirectory(plainRoot);
        }
    }

    // ---- read, save, revert ----

    [Fact]
    public async Task A_modified_file_comes_with_its_diff_against_head()
    {
        Write("agents/scout.agent.md", Agent.Replace("Finds code.", "Finds more code."));

        var detail = await ReadAsync("agents/scout.agent.md");

        Assert.Contains("+description: Finds more code.", detail.Diff);
        Assert.Contains("-description: Finds code.", detail.Diff);
    }

    [Fact]
    public async Task Save_writes_the_file_and_the_new_hash_differs()
    {
        var before = await ReadAsync("agents/scout.agent.md");
        var text = Agent.Replace("Finds code.", "Finds code fast.");

        var reply = await _agents.SaveAsync(new AgentSave("agents/scout.agent.md", text, before.Hash), default);

        Assert.Equal(AgentOutcome.Done, reply.Outcome);
        var after = await ReadAsync("agents/scout.agent.md");
        Assert.Equal(text, after.Text);
        Assert.NotEqual(before.Hash, after.Hash);
        Assert.Empty(Directory.GetFiles(Repo("agents"), "*.tmp-*"));
    }

    [Fact]
    public async Task Save_with_a_stale_hash_is_refused_and_writes_nothing()
    {
        var before = await ReadAsync("agents/scout.agent.md");
        Write("agents/scout.agent.md", Agent + "hand edit\n");

        var reply = await _agents.SaveAsync(
            new AgentSave("agents/scout.agent.md", Agent.Replace("Finds", "Seeks"), before.Hash),
            default);

        Assert.Equal(AgentOutcome.Stale, reply.Outcome);
        Assert.Equal(Agent + "hand edit\n", File.ReadAllText(Repo("agents/scout.agent.md")));
    }

    [Fact]
    public async Task Save_with_bad_frontmatter_lists_the_reasons_and_writes_nothing()
    {
        var before = await ReadAsync("agents/scout.agent.md");

        var reply = await _agents.SaveAsync(
            new AgentSave("agents/scout.agent.md", "---\nname: wrong\n---\n", before.Hash),
            default);

        Assert.Equal(AgentOutcome.Invalid, reply.Outcome);
        Assert.Equal(2, reply.Reasons.Count);
        Assert.Equal(Agent, File.ReadAllText(Repo("agents/scout.agent.md")));
    }

    [Theory]
    [InlineData("agents/new.agent.md")]
    [InlineData("agents/../repos.yml")]
    [InlineData("../outside.agent.md")]
    public async Task Save_never_creates_a_file_or_leaves_the_folders(string path)
    {
        var reply = await _agents.SaveAsync(new AgentSave(path, Agent, "x"), default);

        Assert.Equal(AgentOutcome.NotFound, reply.Outcome);
        Assert.False(File.Exists(Repo("agents/new.agent.md")));
        Assert.False(File.Exists(Path.Combine(_root, "outside.agent.md")));
    }

    [Fact]
    public async Task Templates_cannot_be_saved_or_reverted_but_can_be_read()
    {
        var detail = await ReadAsync("templates/spec.md");
        var save = await _agents.SaveAsync(new AgentSave("templates/spec.md", "x", detail.Hash), default);
        var revert = await _agents.RevertAsync("templates/spec.md", default);

        Assert.Equal("# Spec\n", detail.Text);
        Assert.Equal(AgentOutcome.Refused, save.Outcome);
        Assert.Equal(AgentOutcome.Refused, revert.Outcome);
        Assert.Equal("# Spec\n", File.ReadAllText(Repo("templates/spec.md")));
    }

    [Fact]
    public async Task Revert_restores_the_file_from_head()
    {
        Write("agents/scout.agent.md", "changed\n");

        var reply = await _agents.RevertAsync("agents/scout.agent.md", default);

        Assert.Equal(AgentOutcome.Done, reply.Outcome);
        var restored = File.ReadAllText(Repo("agents/scout.agent.md")).Replace("\r\n", "\n");
        Assert.Equal(Agent, restored);
    }

    [Fact]
    public async Task Revert_of_an_untracked_file_is_refused()
    {
        Write("skills/checklist/extra.md", "x");

        var reply = await _agents.RevertAsync("skills/checklist/extra.md", default);

        Assert.Equal(AgentOutcome.Refused, reply.Outcome);
        Assert.True(File.Exists(Repo("skills/checklist/extra.md")));
    }

    [Fact]
    public async Task A_file_with_windows_line_endings_keeps_them_when_saved()
    {
        Write("agents/scout.agent.md", Agent.Replace("\n", "\r\n"));
        var before = await ReadAsync("agents/scout.agent.md");

        await _agents.SaveAsync(
            new AgentSave("agents/scout.agent.md", Agent.Replace("Body", "New body"), before.Hash),
            default);

        var expected = Agent.Replace("Body", "New body").Replace("\n", "\r\n");
        Assert.Equal(expected, File.ReadAllText(Repo("agents/scout.agent.md")));
    }

    // ---- install, drift and sync ----

    [Fact]
    public async Task Sync_copies_the_files_and_keeps_extra_files_of_the_home()
    {
        Directory.CreateDirectory(Path.Combine(_home, "agents"));
        File.WriteAllText(Path.Combine(_home, "agents", "mine.agent.md"), "mine");

        await _sync.Request();

        Assert.Equal(Agent, File.ReadAllText(Path.Combine(_home, "agents", "scout.agent.md")));
        Assert.Equal(Skill, File.ReadAllText(Path.Combine(_home, "skills", "checklist", "SKILL.md")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_home, "agents", "mine.agent.md")));
        Assert.False(Directory.Exists(Path.Combine(_home, "templates")));
        var listing = await _agents.ListAsync(default);
        Assert.False(listing.IsOutOfSync);
        Assert.Equal(["agents/mine.agent.md"], listing.Extra);
    }

    [Fact]
    public async Task A_changed_source_file_is_drift_until_the_next_sync()
    {
        await _sync.Request();
        Write("agents/scout.agent.md", Agent + "newer\n");

        Assert.True(_files.CheckDrift().IsOutOfSync);
        await _sync.Request();

        Assert.False(_files.CheckDrift().IsOutOfSync);
    }

    [Fact]
    public async Task Sync_waits_for_the_running_job_and_then_copies()
    {
        var slot = await _commands.HoldJobSlotAsync(default);

        var syncTask = _sync.Request();
        await Task.Delay(200);
        Assert.False(syncTask.IsCompleted);
        Assert.False(File.Exists(Path.Combine(_home, "agents", "scout.agent.md")));

        slot.Dispose();
        await syncTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(File.Exists(Path.Combine(_home, "agents", "scout.agent.md")));
    }

    [Fact]
    public async Task A_save_asks_for_a_sync()
    {
        var slot = await _commands.HoldJobSlotAsync(default);
        var before = await ReadAsync("agents/scout.agent.md");
        var text = Agent.Replace("Finds code.", "Finds code fast.");

        await _agents.SaveAsync(new AgentSave("agents/scout.agent.md", text, before.Hash), default);
        Assert.False(File.Exists(Path.Combine(_home, "agents", "scout.agent.md")));
        slot.Dispose();

        await Wait.UntilAsync(() => TextOrNull(Path.Combine(_home, "agents", "scout.agent.md")) == text);
    }
}
