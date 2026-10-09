using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

/// <summary>
/// Copies the shared agents/ and skills/ to ~/.copilot/ at startup, so Copilot CLI always
/// uses the version checked out in this repo (design doc section 11, versioning rule).
/// A product repo's own .github/agents/ copy still wins over these.
/// </summary>
public sealed class AgentInstaller(RunnerConfig config, ILogger<AgentInstaller> logger)
{
    public void Install()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var copilotHome = Path.Combine(userProfile, ".copilot");

        foreach (var folder in new[] { "agents", "skills" })
        {
            var sourceFolder = config.ResolvePath(folder);
            if (!Directory.Exists(sourceFolder))
            {
                continue;
            }

            var targetFolder = Path.Combine(copilotHome, folder);
            Workspace.CopyDirectory(sourceFolder, targetFolder);
            logger.LogInformation("Installed shared {Folder} to {Target}", folder, targetFolder);
        }
    }
}
