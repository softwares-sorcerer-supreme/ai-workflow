using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

/// <summary>
/// Copies the shared agents/ and skills/ to ~/.copilot/ at startup, so Copilot CLI always
/// uses the version checked out in this repo (design doc section 11, versioning rule).
/// A product repo's own .github/agents/ copy still wins over these.
/// </summary>
public sealed class AgentInstaller(RunnerConfig config, ILogger<AgentInstaller> log)
{
    public void Install()
    {
        var copilotHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        foreach (var folder in new[] { "agents", "skills" })
        {
            var from = config.ResolvePath(folder);
            if (!Directory.Exists(from)) continue;
            var to = Path.Combine(copilotHome, folder);
            Workspace.CopyDirectory(from, to);
            log.LogInformation("Installed shared {Folder} to {Target}", folder, to);
        }
    }
}
