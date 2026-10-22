using AiRunner.Configuration;

namespace AiRunner.Hosting;

/// <summary>One log line per change of repos.yml, the same from the poller and from the Settings screen's read.</summary>
public static class ConfigReloadLog
{
    public static void LogReload(this ILogger logger, ConfigReload reload)
    {
        if (!reload.IsChanged)
        {
            return;
        }

        if (reload.Error is null)
        {
            logger.LogInformation("repos.yml changed: config version {Version} applied", reload.Version);
        }
        else
        {
            logger.LogError("repos.yml has errors, the runner keeps its config: {Error}", reload.Error);
        }
    }
}
