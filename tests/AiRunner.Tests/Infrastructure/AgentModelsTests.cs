using AiRunner.Configuration;
using AiRunner.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Infrastructure;

/// <summary>agent_models: written into the installed copy of an agent, never into the source file.</summary>
public sealed class AgentModelsTests : IDisposable
{
    private const string Scout = "---\nname: scout\ndescription: Finds code.\nmodel: old-model\n---\n\nBody\n";
    private const string Reviewer = "---\nname: reviewer\ndescription: Reviews.\n---\n\nBody\n";

    private readonly string _root = Directory.CreateTempSubdirectory("ai-runner-agent-models-").FullName;
    private readonly RunnerConfig _config;
    private readonly string _home;

    public AgentModelsTests()
    {
        var repo = Path.Combine(_root, "repo");
        _home = Path.Combine(_root, "copilot-home");
        _config = new RunnerConfig { RootDir = repo };
        Directory.CreateDirectory(Path.Combine(repo, "agents"));
        File.WriteAllText(Path.Combine(repo, "agents", "scout.agent.md"), Scout);
        File.WriteAllText(Path.Combine(repo, "agents", "reviewer.agent.md"), Reviewer);
    }

    public void Dispose() => FileSystem.DeleteDirectory(_root);

    private void Install() =>
        new AgentInstaller(RunnerConfigSource.Fixed(_config), NullLogger<AgentInstaller>.Instance).Install(_home);

    private string Installed(string name) => File.ReadAllText(Path.Combine(_home, "agents", name + ".agent.md"));

    private string Source(string name) => File.ReadAllText(Path.Combine(_root, "repo", "agents", name + ".agent.md"));

    [Fact]
    public void The_model_is_written_into_the_copy_and_the_source_is_unchanged()
    {
        _config.AgentModels["scout"] = "claude-haiku-5.5";
        _config.AgentModels["reviewer"] = "claude-sonnet-5.5";

        Install();

        Assert.Contains("model: claude-haiku-5.5", Installed("scout"));
        Assert.DoesNotContain("old-model", Installed("scout"));
        Assert.Contains("model: claude-sonnet-5.5", Installed("reviewer"));
        Assert.Equal(Scout, Source("scout"));
        Assert.Equal(Reviewer, Source("reviewer"));
    }

    [Fact]
    public void Without_agent_models_the_copy_equals_the_source()
    {
        Install();

        Assert.Equal(Scout, Installed("scout"));
    }

    [Fact]
    public void A_model_outside_allowed_models_or_an_unknown_agent_is_skipped()
    {
        _config.AllowedModels = ["claude-sonnet-5.5"];
        _config.AgentModels["scout"] = "claude-haiku-5.5";
        _config.AgentModels["nobody"] = "claude-sonnet-5.5";

        Install();

        Assert.Equal(Scout, Installed("scout"));
    }

    [Fact]
    public void A_model_set_from_the_config_is_not_reported_as_drift()
    {
        _config.AgentModels["scout"] = "claude-haiku-5.5";
        var files = new AgentFiles(RunnerConfigSource.Fixed(_config), new CopilotHome(_home), new ProcessRunner());
        Install();

        var drift = files.CheckDrift();

        Assert.False(drift.IsOutOfSync);
        Assert.Equal(AgentStates.InSync, files.SyncStateOf("agents/scout.agent.md"));

        _config.AgentModels["scout"] = "claude-opus-5.5";

        Assert.Equal(AgentStates.Differs, files.SyncStateOf("agents/scout.agent.md"));
    }
}
