using System.Text.Json.Nodes;
using AiRunner.Configuration;
using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

/// <summary>
/// What the Settings screen shows: repos.yml as JSON (secrets as <c>{ isSet }</c>), the hash a save must start from,
/// and what the runner made of it.
/// </summary>
/// <param name="File">The applied (last good) file; when the one on disk is broken, <see cref="Error"/> says why.</param>
/// <param name="Hash">Hash of the file on disk now: a save is refused when it changed since this read.</param>
/// <param name="HasComments">The file has comments, which a save drops (the screen warns before the first save).</param>
/// <param name="RestartNeeded">Keys saved but applied only at the next start.</param>
public sealed record SettingsView(
    JsonObject? File,
    string Hash,
    int Version,
    string Path,
    string? Error,
    bool HasComments,
    IReadOnlyList<string> RestartNeeded,
    IReadOnlyList<ProjectEffective> Effective);

/// <summary>An edited file and the hash of the file it was read from.</summary>
public sealed record SettingsSave(JsonObject File, string BaseHash);

public enum SettingsSaveOutcome
{
    Saved,

    /// <summary>repos.yml changed since the screen read it (a hand edit): nothing was saved.</summary>
    Stale,

    /// <summary>The resolver refused the new file: nothing was saved.</summary>
    Invalid,
}

/// <summary>The answer to a save, with a message the screen shows as it is (never a secret).</summary>
public sealed record SettingsSaveResult(SettingsSaveOutcome Outcome, string Message);

/// <summary>
/// The Settings screen's way into the runner (docs/specs/dashboard-control.md section 8), shared with the web app like
/// <see cref="IRunnerSetup"/>. It never takes or returns the token: that is not a key of repos.yml.
/// </summary>
public interface IRunnerSettings
{
    SettingsView Read();

    SettingsSaveResult Save(SettingsSave save);
}

/// <summary>
/// Reads and saves repos.yml for the Settings screen. A read first applies a hand edit (like the next poll would). A
/// save starts from the file the screen read (else stale), goes through the strict resolver (else invalid, nothing
/// written), keeps a backup, replaces the file, applies the new config at once and wakes the poller. A running job
/// keeps the config it started with (<see cref="RunnerServices"/>).
/// </summary>
public sealed class RunnerSettings(
    RunnerConfigSource configSource,
    ConfigFileStore store,
    RunnerCommands commands,
    ILogger<RunnerSettings> logger) : IRunnerSettings
{
    private string ConfigPath =>
        configSource.Path ?? throw new InvalidOperationException("This runner has no repos.yml to edit.");

    public SettingsView Read()
    {
        // A hand edit is applied here too, not only at the next poll: after a 409 the screen reloads and must get the
        // new hash at once.
        var reload = configSource.ReloadIfChanged();
        logger.LogReload(reload);

        var state = configSource.State;
        var file = SettingsJson.Read(state.Text);
        var effective = SettingsJson.Effective(file);
        var hasComments = SettingsJson.HasComments(state.Text);

        return new SettingsView(
            file,
            state.FileHash,
            state.Version,
            ConfigPath,
            state.Error,
            hasComments,
            state.RestartNeeded,
            effective);
    }

    /// <summary>
    /// Check, write and apply happen under the reload lock (<see cref="RunnerConfigSource.TryReplace"/>), so two saves
    /// at once cannot interleave: the second one starts from a hash that is no longer on disk and is stale.
    /// </summary>
    public SettingsSaveResult Save(SettingsSave save)
    {
        var path = ConfigPath;

        // Secrets kept as they are come from the applied file: the one on disk may be a broken hand edit.
        string newText;
        bool isReplaced;
        try
        {
            newText = SettingsJson.Write(save.File, configSource.State.Text);
            isReplaced = configSource.TryReplace(save.BaseHash, newText, text => store.Write(path, text));
        }
        catch (InvalidOperationException exception)
        {
            return new SettingsSaveResult(SettingsSaveOutcome.Invalid, exception.Message);
        }

        if (!isReplaced)
        {
            return new SettingsSaveResult(
                SettingsSaveOutcome.Stale,
                "repos.yml changed since this page read it (a hand edit?). " +
                "Reload the settings and make the change again.");
        }

        commands.WakePoller();
        var state = configSource.State;
        logger.LogInformation("repos.yml saved from the dashboard: config version {Version} applied", state.Version);

        var restartNeeded = state.RestartNeeded;
        var message = restartNeeded.Count == 0
            ? "Saved and applied."
            : $"Saved and applied; these take effect at the next start: {string.Join(", ", restartNeeded)}.";

        return new SettingsSaveResult(SettingsSaveOutcome.Saved, message);
    }
}
