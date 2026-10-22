using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

/// <summary>
/// The one rule for <c>agent_models</c>: an agent gets the model of its entry when that model is allowed. The installer
/// writes it into the copy and the drift check hashes the same result, so they cannot disagree.
/// </summary>
public static class AgentModels
{
    /// <summary>The model to write into the installed copy of <paramref name="agent"/>, or null for none.</summary>
    public static string? ModelFor(RunnerConfig config, string agent)
    {
        var hasModel = config.AgentModels.TryGetValue(agent, out var model);

        return hasModel && ModelSetting.IsAllowed(config.AllowedModels, model!) ? model : null;
    }
}
