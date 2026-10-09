using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiRunner.Configuration;

/// <summary>
/// Everything from repos.yml. Tokens are never here (user secrets or environment).
/// Relative paths are resolved against the folder of repos.yml, so the runner works
/// the same from any current directory.
/// </summary>
public sealed class RunnerConfig
{
    public GitBucketConfig GitBucket { get; set; } = new();
    public int PollIntervalSeconds { get; set; } = 90;
    public string ScopeLabel { get; set; } = "ai";
    public CopilotConfig Copilot { get; set; } = new();
    public PathsConfig Paths { get; set; } = new();
    public int LogRetentionDays { get; set; } = 30;
    public GitConfig Git { get; set; } = new();
    public AttachmentsConfig Attachments { get; set; } = new();
    public List<RepoConfig> Repos { get; set; } = [];

    /// <summary>Folder of repos.yml; the shared agents/, skills/ and templates/ live here.</summary>
    [YamlIgnore] public string RootDir { get; private set; } = "";

    public string ResolvePath(string path)
    {
        var combinedPath = Path.Combine(RootDir, path);
        return Path.GetFullPath(combinedPath);
    }

    public RepoConfig? FindRepo(string fullName) =>
        Repos.FirstOrDefault(repo => string.Equals(repo.FullName, fullName, StringComparison.OrdinalIgnoreCase));

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
            throw new InvalidOperationException("gitBucket.baseUrl is missing in " + path);
        }

        foreach (var repo in config.Repos)
        {
            foreach (var unknownWorkflow in repo.Workflows.Where(name => !Workflows.All.Contains(name)))
            {
                throw new InvalidOperationException(
                    $"{repo}: unknown workflow '{unknownWorkflow}'. Use: {string.Join(", ", Workflows.All)}");
            }
        }

        return config;
    }

    public static RunnerConfig Parse(string yaml) =>
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<RunnerConfig>(yaml) ?? new RunnerConfig();
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
/// into the clone so the agent can read them. Only links on the GitBucket host are fetched.
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

public sealed class RepoConfig
{
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Workflows { get; set; } = [];
    public Dictionary<string, string> Models { get; set; } = [];
    public CommandsConfig Commands { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();
    public ReviewConfig Review { get; set; } = new();
    public ImplementConfig Implement { get; set; } = new();

    public bool AutoStartFirstSubIssue { get; set; }

    public string FullName => $"{Owner}/{Name}";

    public bool IsEnabled(string workflow) => Workflows.Contains(workflow, StringComparer.OrdinalIgnoreCase);

    /// <summary>Model for a workflow, or null for the Copilot default.</summary>
    public string? ModelFor(string workflow) =>
        Models.TryGetValue(workflow, out var model) && !string.IsNullOrWhiteSpace(model) ? model : null;

    public override string ToString() => FullName;
}

/// <summary>
/// Build, test and lint commands of the product repo, in any language ("npm test", "mvn -q verify",
/// "dotnet test X.sln"). Copilot may run the tools of these commands with any arguments; the runner
/// runs them again after the agent stops, sends failures back for a fix round, and marks the PR WIP
/// when they still fail. Empty = not checked.
/// </summary>
public sealed class CommandsConfig
{
    /// <summary>
    /// Installs dependencies after the checkout ("npm ci", "pip install -r requirements.txt").
    /// The work folder is cleaned before each job (git clean -fdx), so ignored folders like
    /// node_modules are gone. Run by the runner before Copilot starts; the agent may run it too.
    /// </summary>
    public string? Setup { get; set; }

    public string? Build { get; set; }

    public string? Test { get; set; }

    /// <summary>Linter or format check ("npm run lint", "dotnet format --verify-no-changes"). Optional.</summary>
    public string? Lint { get; set; }

    /// <summary>Extra Copilot shell permission patterns, e.g. "npm:*" or "./gradlew:*".</summary>
    public List<string> AllowShell { get; set; } = [];

    /// <summary>Every command whose tool the agent may run: setup, build, test, lint.</summary>
    public IEnumerable<string> AllowedForAgent() => NonEmpty(Setup, Build, Test, Lint);

    /// <summary>Build, test and lint, run by the runner after the agent stops.</summary>
    public IEnumerable<string> Verification() => NonEmpty(Build, Test, Lint);

    private static IEnumerable<string> NonEmpty(params string?[] commands) =>
        commands
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(command => command!.Trim());
}

public sealed class LimitsConfig
{
    public int MaxChangedFiles { get; set; } = 15;
    public int MaxDiffLines { get; set; } = 800;
    public int MaxAnalysisRounds { get; set; } = 5;

    /// <summary>Review fixes per PR (each an @ai comment answered with commits); counted from the bot's replies on the PR.</summary>
    public int MaxReviewFixes { get; set; } = 5;

    /// <summary>Still "simple"? Above the limits Workflow 2 opens no PR.</summary>
    public bool Allows(int changedFiles, int changedLines) =>
        changedFiles <= MaxChangedFiles && changedLines <= MaxDiffLines;
}

/// <summary>Workflow 2 settings beyond the commands and the size limits.</summary>
public sealed class ImplementConfig
{
    /// <summary>
    /// Extra Copilot runs the runner may start when its own checks fail or PR_BODY.md is incomplete
    /// after the agent stopped. Each one costs a premium request. 0 = never; the PR is then WIP at once.
    /// </summary>
    public int MaxFixRounds { get; set; } = 2;
}

public sealed class ReviewConfig
{
    /// <summary>
    /// "scoped" (default) = only PRs labelled with the scope label, plus PRs this runner opened itself from ai/* or feat/issue-*.
    /// "all" = every open PR. Scoped is the default so the bot does not comment on colleagues' PRs uninvited,
    /// and several runners do not all review the same PR.
    /// </summary>
    public string Scope { get; set; } = "scoped";

    /// <summary>Glob patterns; a PR that changes only these files is not reviewed.</summary>
    public List<string> SkipPaths { get; set; } = [];

    public bool IsScoped => string.Equals(Scope, "scoped", StringComparison.OrdinalIgnoreCase);
}
