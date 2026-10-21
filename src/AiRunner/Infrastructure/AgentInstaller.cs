using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

/// <summary>The folder Copilot CLI reads its agents and skills from (<c>~/.copilot</c>); tests pass a temp folder.</summary>
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
public sealed class AgentInstaller(RunnerConfig config, ILogger<AgentInstaller> logger)
{
    public void Install(string copilotHome)
    {
        foreach (var folder in AgentFiles.SharedFolders)
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
