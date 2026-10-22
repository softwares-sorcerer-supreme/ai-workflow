using YamlDotNet.Serialization;

namespace AiRunner.Configuration;

/// <summary>
/// Everything from repos.yml, after the defaults and the project overrides are merged. Tokens are never here
/// (user secrets or environment), and neither is the web password (<see cref="ConfigFile"/>). Relative paths are
/// resolved against the folder of repos.yml, so the runner works the same from any current directory.
/// </summary>
public sealed class RunnerConfig
{
    public CodeHostConfig CodeHost { get; set; } = new();
    public int PollIntervalSeconds { get; set; } = 90;

    /// <summary>Names this runner in claims. Empty = the machine user name.</summary>
    public string RunnerName { get; set; } = "";

    public CopilotConfig Copilot { get; set; } = new();
    public PathsConfig Paths { get; set; } = new();
    public int LogRetentionDays { get; set; } = 30;
    public GitConfig Git { get; set; } = new();
    public AttachmentsConfig Attachments { get; set; } = new();
    public DashboardConfig Dashboard { get; set; } = new();

    /// <summary>
    /// Empty = no check. Otherwise the only models settings, agent files and issue overrides may name.
    /// </summary>
    public List<string> AllowedModels { get; set; } = [];

    /// <summary>
    /// Subagent name to model; <c>AgentInstaller</c> writes it into the installed copy (the source stays).
    /// </summary>
    public Dictionary<string, string> AgentModels { get; set; } = [];

    public List<ProjectConfig> Projects { get; set; } = [];

    /// <summary>Folder of repos.yml; the shared agents/, skills/ and templates/ live here.</summary>
    [YamlIgnore] public string RootDir { get; set; } = "";

    public string ResolvePath(string path)
    {
        var combinedPath = Path.Combine(RootDir, path);
        return Path.GetFullPath(combinedPath);
    }

    /// <summary>logs/jobs/: one folder per job run. The one place the writer (JobLog) and the dashboard's viewer agree on.</summary>
    [YamlIgnore] public string JobLogsFolder => Path.Combine(ResolvePath(Paths.Logs), "jobs");

    /// <summary>The project a repo belongs to (any of its components' repos), or null.</summary>
    public ProjectConfig? FindProject(string repo) =>
        Projects.FirstOrDefault(project => project.HasRepo(repo));

    /// <summary>The project with this name (as in repos.yml, case-sensitive), or null.</summary>
    public ProjectConfig? ProjectNamed(string name) =>
        Projects.FirstOrDefault(project => project.Name == name);

    /// <summary>Every distinct repo of every project, in file order.</summary>
    public IEnumerable<string> AllRepos() =>
        Projects
            .SelectMany(project => project.Repos)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The trigger keyword of the project a repo belongs to; "@ai" for an unknown repo.</summary>
    public string TriggerKeywordOf(string repo) => FindProject(repo)?.Settings.TriggerKeyword ?? "@ai";

    public string EffectiveRunnerName() =>
        string.IsNullOrWhiteSpace(RunnerName) ? Environment.UserName : RunnerName.Trim();

    public static RunnerConfig Load(string path) => LoadFile(path).Config;

    /// <summary>The config plus what repos.yml holds that must stay out of it (the web password).</summary>
    public static ConfigFile LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found: {Path.GetFullPath(path)}");
        }

        var yaml = File.ReadAllText(path);
        var fullPath = Path.GetFullPath(path);

        return FromText(yaml, fullPath);
    }

    /// <summary>
    /// The text of repos.yml at <paramref name="fullPath"/>, checked like at startup: the one way in for the start,
    /// a reload and a save, so a file the runner would refuse to start with is never applied.
    /// </summary>
    public static ConfigFile FromText(string yaml, string fullPath)
    {
        var configFile = ParseFile(yaml);
        configFile.Config.RootDir = Path.GetDirectoryName(fullPath)!;
        RequireBaseUrl(configFile.Config, fullPath);

        return configFile;
    }

    internal static void RequireBaseUrl(RunnerConfig config, string path)
    {
        if (string.IsNullOrWhiteSpace(config.CodeHost.BaseUrl))
        {
            throw new InvalidOperationException(
                $"code_host.base_url (or the old git_bucket.base_url) is missing in {path}");
        }
    }

    /// <summary>Reads repos.yml text: strict snake_case keys, defaults merged into each project, everything validated.</summary>
    public static RunnerConfig Parse(string yaml) => ParseFile(yaml).Config;

    public static ConfigFile ParseFile(string yaml)
    {
        var file = ConfigResolver.Deserialize(yaml);

        return ConfigResolver.Resolve(file);
    }
}

/// <summary>
/// repos.yml as the runner's entry point needs it: the config, and the web password beside it rather than inside it,
/// because the config is handed to the dashboard and no secret may reach the dashboard (docs/adr/0001).
/// </summary>
/// <param name="WebPassword">
/// code_host.web_password (or git_bucket.web_password): the sign-in password of the token's user, for closing
/// issues through the web page
/// (/api/v3 cannot). Empty = not set. Never put it in an API response or a log.
/// </param>
public sealed record ConfigFile(RunnerConfig Config, string WebPassword);

/// <summary>Workflow names used in repos.yml (workflows: and models:).</summary>
public static class Workflows
{
    public const string Analyze = "analyze";
    public const string Breakdown = "breakdown";
    public const string Implement = "implement";
    public const string Review = "review";
    public const string Fix = "fix";

    public static readonly string[] All = [Analyze, Breakdown, Implement, Review, Fix];

    /// <summary>The workflows that run Copilot, so the only keys models: accepts (the breakdown only reads the analysis).</summary>
    public static readonly string[] WithModel = [Analyze, Implement, Review, Fix];
}

/// <summary>The code_host block without the web password. Kind and URL take effect at the next start.</summary>
public sealed class CodeHostConfig
{
    /// <summary>One of <see cref="CodeHostKinds.All"/>; it picks the provider (Infrastructure/CodeHost).</summary>
    public string Kind { get; set; } = CodeHostKinds.GitBucket;
    public string BaseUrl { get; set; } = "";
}

/// <summary>The code hosts the runner has a provider for (code_host.kind).</summary>
public static class CodeHostKinds
{
    public const string GitBucket = "gitbucket";

    public static readonly string[] All = [GitBucket];
}

public sealed class CopilotConfig
{
    public int AnalyzeTimeoutMinutes { get; set; } = 15;
    public int ReviewTimeoutMinutes { get; set; } = 15;
    public int ImplementTimeoutMinutes { get; set; } = 30;

    /// <summary>Extra flags for every run, e.g. --disable-builtin-mcps so the agent cannot use the GitHub MCP server.</summary>
    public List<string> ExtraArgs { get; set; } = [];

    /// <summary>Last, as in repos.yml (the key order of a saved file follows the properties).</summary>
    public string Command { get; set; } = "copilot";
}

/// <summary>
/// Files people attach to issues, comments and PRs (GitBucket's _attached links). The runner downloads them
/// into the workspace so the agent can read them. Only links on the GitBucket host are fetched.
/// </summary>
public sealed class AttachmentsConfig
{
    public bool Enabled { get; set; } = true;
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxFiles { get; set; } = 20;
    public long MaxTotalBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Also attach images and PDFs to the Copilot prompt (--attachment); otherwise they are only on disk.</summary>
    public bool PassToCopilot { get; set; } = true;

    /// <summary>At most this many files go on the prompt; each one costs tokens.</summary>
    public int MaxPassedToCopilot { get; set; } = 5;

    /// <summary>Never downloaded: nobody should open an executable from a ticket.</summary>
    public List<string> SkipExtensions { get; set; } =
        [".exe", ".dll", ".msi", ".bat", ".cmd", ".ps1", ".sh", ".com", ".scr", ".jar", ".vbs"];
}

/// <summary>
/// The local read-only dashboard (global only, never per project). Off by default so a runner never opens a port
/// its owner did not ask for; it listens on 127.0.0.1 only.
/// </summary>
public sealed class DashboardConfig
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 5180;
}

public sealed class PathsConfig
{
    public string Work { get; set; } = "work";
    public string Logs { get; set; } = "logs";
    public string State { get; set; } = "ai-runner.db";
}

public sealed class GitConfig
{
    public string UserName { get; set; } = "ai-runner";
    public string UserEmail { get; set; } = "ai-runner@localhost";
}
