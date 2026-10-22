using System.Security.Cryptography;
using System.Text;

namespace AiRunner.Configuration;

/// <summary>
/// What a reload found: whether the file changed since it was last read, why it was refused, and the version of the
/// config applied after it.
/// </summary>
public sealed record ConfigReload(bool IsChanged, string? Error, int Version);

/// <summary>
/// Everything <see cref="RunnerConfigSource"/> knows at one moment, swapped as a whole, so a reader never pairs the
/// text of one file with the hash of another.
/// </summary>
/// <param name="Config">The applied config.</param>
/// <param name="Version">1 at the start, one more for every config applied since.</param>
/// <param name="Error">Why the file on disk is not applied; null when it is.</param>
/// <param name="Text">The text of the applied config (the last good file).</param>
/// <param name="FileHash">
/// Hash of the file last read from disk (or saved), applied or not: a save starts from it.
/// </param>
/// <param name="RestartNeeded">
/// Keys whose new value waits for a restart (schema paths, e.g. <c>dashboard.port</c>).
/// </param>
public sealed record ConfigState(
    RunnerConfig Config,
    int Version,
    string? Error,
    string Text,
    string FileHash,
    IReadOnlyList<string> RestartNeeded);

/// <summary>
/// The config the runner works with now, live (docs/adr/0004). A new repos.yml goes through the same strict path as
/// the start (<see cref="RunnerConfig.FromText"/>); one that fails never replaces the working config, it only sets
/// the error. A published config is never changed afterwards, so whoever took <see cref="Current"/> keeps a stable
/// snapshot: a job takes it once, in its DI scope, and uses it to its end.
/// The keys that cannot change in a running process (<c>code_host</c>, <c>paths</c>, <c>dashboard</c>,
/// <c>runner_name</c>) keep their startup values in every new config and are listed in
/// <see cref="ConfigState.RestartNeeded"/>, so a singleton that reads only those may read <see cref="Current"/> at any
/// time.
/// </summary>
public sealed class RunnerConfigSource
{
    private readonly Lock _lock = new();
    private readonly ConfigFile _startup;
    private volatile ConfigState _state;

    /// <summary>
    /// The keys <see cref="Publish"/> keeps at their startup values, and how to tell that a new file changed one.
    /// <c>SettingsSchemaTests</c> checks that the schema's <see cref="SettingDefinition.IsRestartNeeded"/> marks the
    /// same keys.
    /// </summary>
    private static readonly (string Key, Func<ConfigFile, ConfigFile, bool> IsChanged)[] RestartKeys =
    [
        ("code_host.kind", (startup, loaded) => startup.Config.CodeHost.Kind != loaded.Config.CodeHost.Kind),
        ("code_host.base_url", (startup, loaded) => startup.Config.CodeHost.BaseUrl != loaded.Config.CodeHost.BaseUrl),
        ("code_host.web_password", (startup, loaded) => startup.WebPassword != loaded.WebPassword),
        ("paths.work", (startup, loaded) => startup.Config.Paths.Work != loaded.Config.Paths.Work),
        ("paths.logs", (startup, loaded) => startup.Config.Paths.Logs != loaded.Config.Paths.Logs),
        ("paths.state", (startup, loaded) => startup.Config.Paths.State != loaded.Config.Paths.State),
        ("dashboard.enabled", (startup, loaded) => startup.Config.Dashboard.Enabled != loaded.Config.Dashboard.Enabled),
        ("dashboard.port", (startup, loaded) => startup.Config.Dashboard.Port != loaded.Config.Dashboard.Port),
        ("runner_name", (startup, loaded) => startup.Config.RunnerName != loaded.Config.RunnerName),
    ];

    /// <summary>The schema paths of the keys that keep their startup values.</summary>
    public static IEnumerable<string> RestartNeededKeys => RestartKeys.Select(entry => entry.Key);

    private RunnerConfigSource(ConfigFile startup, string? path, string text)
    {
        _startup = startup;
        _state = new ConfigState(startup.Config, Version: 1, Error: null, text, HashOf(text), RestartNeeded: []);
        Path = path;
    }

    /// <summary>Full path of repos.yml; null for a source without a file (tests, the OpenAPI export).</summary>
    public string? Path { get; }

    /// <summary>All of the source's state at once; read it once per answer.</summary>
    public ConfigState State => _state;

    public RunnerConfig Current => _state.Config;

    /// <summary>The web password repos.yml held at the start; applied once (the Setup page is the live way).</summary>
    public string StartupWebPassword => _startup.WebPassword;

    /// <summary>Reads repos.yml for the start; a bad file stops the start, as before.</summary>
    public static RunnerConfigSource Load(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Config file not found: {fullPath}");
        }

        var text = File.ReadAllText(fullPath);
        var startup = RunnerConfig.FromText(text, fullPath);

        return new RunnerConfigSource(startup, fullPath, text);
    }

    /// <summary>A source that always holds <paramref name="config"/>: no file, no reload.</summary>
    public static RunnerConfigSource Fixed(RunnerConfig config) =>
        new(new ConfigFile(config, ""), path: null, text: "");

    public static string HashOf(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Applies repos.yml when its text changed since it was last read (called at the start of every poll). The
    /// error of a refused file is kept until a good file replaces it; the caller logs a changed result.
    /// </summary>
    public ConfigReload ReloadIfChanged()
    {
        if (Path is null)
        {
            return Unchanged();
        }

        lock (_lock)
        {
            string text;
            try
            {
                text = File.ReadAllText(Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Missing or locked (an editor saving it): the next poll reads it again.
                return Unchanged();
            }

            var hash = HashOf(text);
            if (hash == _state.FileHash)
            {
                return Unchanged();
            }

            try
            {
                var loaded = RunnerConfig.FromText(text, Path);
                Publish(loaded, text);
            }
            catch (InvalidOperationException exception)
            {
                _state = _state with { Error = exception.Message, FileHash = hash };
            }

            return new ConfigReload(IsChanged: true, _state.Error, _state.Version);
        }
    }

    /// <summary>
    /// Replaces repos.yml with <paramref name="text"/> (a dashboard save) and applies it, all under the reload lock:
    /// only when the file on disk still has <paramref name="baseHash"/> (else false and nothing is written, so a hand
    /// edit is never overwritten), and only after the text was checked (an <see cref="InvalidOperationException"/>
    /// leaves the file untouched). <paramref name="writeFile"/> does the write (backup, temp file, move).
    /// </summary>
    public bool TryReplace(string baseHash, string text, Action<string> writeFile)
    {
        var path = Path ?? throw new InvalidOperationException("This runner has no repos.yml to save.");

        lock (_lock)
        {
            var onDisk = File.ReadAllText(path);
            if (HashOf(onDisk) != baseHash)
            {
                return false;
            }

            var loaded = RunnerConfig.FromText(text, path);
            writeFile(text);
            Publish(loaded, text);

            return true;
        }
    }

    /// <summary>Called under the lock. Restart-needed keys keep their startup values.</summary>
    private void Publish(ConfigFile loaded, string text)
    {
        var startup = _startup.Config;
        var config = loaded.Config;
        var restartNeeded = ChangedRestartKeys(loaded);

        config.CodeHost = startup.CodeHost;
        config.Paths = startup.Paths;
        config.Dashboard = startup.Dashboard;
        config.RunnerName = startup.RunnerName;
        config.RootDir = startup.RootDir;

        var hash = HashOf(text);
        _state = new ConfigState(config, _state.Version + 1, Error: null, text, hash, restartNeeded);
    }

    private ConfigReload Unchanged()
    {
        var state = _state;

        return new ConfigReload(IsChanged: false, state.Error, state.Version);
    }

    private List<string> ChangedRestartKeys(ConfigFile loaded) =>
        RestartKeys
            .Where(entry => entry.IsChanged(_startup, loaded))
            .Select(entry => entry.Key)
            .ToList();
}
