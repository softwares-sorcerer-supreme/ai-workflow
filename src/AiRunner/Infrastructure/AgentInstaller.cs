using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>
/// The folder Copilot CLI reads its agents and skills from (<c>~/.copilot</c>); tests pass a temp folder.
/// </summary>
public sealed record CopilotHome(string Path)
{
    public static CopilotHome FromUserProfile() =>
        new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot"));
}

/// <summary>
/// Copies the shared agents/ and skills/ to the Copilot home folder, so Copilot CLI always uses the version checked
/// out in this repo (design doc section 11, versioning rule). It runs at startup and, from the dashboard, between
/// jobs (<c>AgentSync</c>). It only copies: a file in the target the repo does not have is never deleted, because
/// the user may use Copilot for other work. A product repo's own .github/agents/ copy still wins over these.
/// </summary>
public sealed class AgentInstaller(RunnerConfigSource configSource, ILogger<AgentInstaller> logger)
{
    /// <summary>
    /// Reads only keys that need a restart (and the root folder), so the live config is always right.
    /// </summary>
    private RunnerConfig Config => configSource.Current;

    public void Install(string copilotHome)
    {
        foreach (var folder in AgentFiles.SharedFolders)
        {
            var sourceFolder = Config.ResolvePath(folder);
            if (!Directory.Exists(sourceFolder))
            {
                continue;
            }

            var targetFolder = Path.Combine(copilotHome, folder);
            Workspace.CopyDirectory(sourceFolder, targetFolder);
            logger.LogInformation("Installed shared {Folder} to {Target}", folder, targetFolder);
        }

        ApplyAgentModels(copilotHome);
    }

    /// <summary>
    /// Writes <c>agent_models</c> into the front matter of the installed copies (never the source files). A name with
    /// no installed agent file, or a model outside <c>allowed_models</c>, is skipped with a warning.
    /// </summary>
    private void ApplyAgentModels(string copilotHome)
    {
        foreach (var (agent, model) in Config.AgentModels)
        {
            if (!ModelSetting.IsAllowed(Config.AllowedModels, model))
            {
                logger.LogWarning(
                    "agent_models.{Agent} names {Model}, which is not in allowed_models: skipped", agent, model);
                continue;
            }

            var agentFile = Path.Combine(copilotHome, "agents", agent + ".agent.md");
            if (!File.Exists(agentFile))
            {
                logger.LogWarning("agent_models.{Agent}: no installed agent file {File}: skipped", agent, agentFile);
                continue;
            }

            var text = File.ReadAllText(agentFile);
            File.WriteAllText(agentFile, AgentFrontmatter.WithModel(text, model));
            logger.LogInformation("Set the model of agent {Agent} to {Model}", agent, model);
        }
    }
}
