using YamlDotNet.Serialization;

namespace AiRunner.Configuration;

/// <summary>
/// Everything from repos.yml, after the defaults and the project overrides are merged. Tokens are never here
/// (user secrets or environment). Relative paths are resolved against the folder of repos.yml, so the runner
/// works the same from any current directory.
/// </summary>
public sealed class RunnerConfig
{
    public GitBucketConfig GitBucket { get; set; } = new();
    public int PollIntervalSeconds { get; set; } = 90;

    /// <summary>Names this runner in claims. Empty = the machine user name.</summary>
    public string RunnerName { get; set; } = "";

    public CopilotConfig Copilot { get; set; } = new();
    public PathsConfig Paths { get; set; } = new();
    public int LogRetentionDays { get; set; } = 30;
    public GitConfig Git { get; set; } = new();
    public AttachmentsConfig Attachments { get; set; } = new();
    public DashboardConfig Dashboard { get; set; } = new();
    public List<ProjectConfig> Projects { get; set; } = [];

    /// <summary>Folder of repos.yml; the shared agents/, skills/ and templates/ live here.</summary>
    [YamlIgnore] public string RootDir { get; private set; } = "";

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

    /// <summary>Every distinct repo of every project, in file order.</summary>
    public IEnumerable<string> AllRepos() =>
        Projects
            .SelectMany(project => project.Repos)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The trigger keyword of the project a repo belongs to; "@ai" for an unknown repo.</summary>
    public string TriggerKeywordOf(string repo) => FindProject(repo)?.Settings.TriggerKeyword ?? "@ai";

    public string EffectiveRunnerName() =>
        string.IsNullOrWhiteSpace(RunnerName) ? Environment.UserName : RunnerName.Trim();

    public static RunnerConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found: {Path.GetFullPath(path)}");
        }

        var yaml = File.ReadAllText(path);
        var config = Parse(yaml);
        var fullPath = Path.GetFullPath(path);
        config.RootDir = Path.GetDirectoryName(fullPath)!;

        if (string.IsNullOrWhiteSpace(config.GitBucket.BaseUrl))
        {
            throw new InvalidOperationException("git_bucket.base_url is missing in " + path);
        }

        return config;
    }

    /// <summary>Reads repos.yml text: strict snake_case keys, defaults merged into each project, everything validated.</summary>
    public static RunnerConfig Parse(string yaml) => ConfigResolver.Resolve(ConfigResolver.Deserialize(yaml));
}

/// <summary>Workflow names used in repos.yml (workflows: and models:).</summary>
public static class Workflows
{
    public const string Analyze = "analyze";
    public const string Breakdown = "breakdown";
    public const string Implement = "implement";
    public const string Review = "review";
    public const string Fix = "fix";

    public static readonly string[] All = [Analyze, Breakdown, Implement, Review, Fix];
}

public sealed class GitBucketConfig
{
    public string BaseUrl { get; set; } = "";
}

public sealed class CopilotConfig
{
    public string Command { get; set; } = "copilot";
    public int AnalyzeTimeoutMinutes { get; set; } = 15;
    public int ReviewTimeoutMinutes { get; set; } = 15;
    public int ImplementTimeoutMinutes { get; set; } = 30;

    /// <summary>Extra flags for every run, e.g. --disable-builtin-mcps so the agent cannot use the GitHub MCP server.</summary>
    public List<string> ExtraArgs { get; set; } = [];
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
