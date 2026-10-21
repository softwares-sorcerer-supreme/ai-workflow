namespace AiRunner.Configuration;

// The shape of repos.yml as it is written (snake_case). Every setting is nullable: "not written" must stay
// different from "written as the default", so a project can override only what it names. ConfigResolver
// merges these into the resolved classes (RunnerConfig, ProjectConfig). Nothing outside Configuration/ sees them.

internal sealed class RunnerFile
{
    public CodeHostFile? CodeHost { get; set; }

    /// <summary>The old name of <see cref="CodeHost"/>; it means kind gitbucket. Both at once stop the start.</summary>
    public GitBucketFile? GitBucket { get; set; }

    public int PollIntervalSeconds { get; set; } = 90;
    public string RunnerName { get; set; } = "";
    public CopilotConfig Copilot { get; set; } = new();
    public PathsConfig Paths { get; set; } = new();
    public int LogRetentionDays { get; set; } = 30;
    public AttachmentsConfig Attachments { get; set; } = new();
    public GitConfig Git { get; set; } = new();
    public DashboardConfig Dashboard { get; set; } = new();
    public SettingsFile Defaults { get; set; } = new();
    public Dictionary<string, ProjectFile> Projects { get; set; } = [];
    public List<LegacyRepoFile> Repos { get; set; } = [];
}

/// <summary>
/// The code_host block. The web password is not copied into <see cref="RunnerConfig"/>: the dashboard gets the
/// config, and no secret may reach it (docs/adr/0001). It travels next to it in <see cref="ConfigFile"/>.
/// </summary>
internal sealed class CodeHostFile
{
    /// <summary>Which provider talks to the host (<see cref="CodeHostKinds"/>). Required: no silent default.</summary>
    public string? Kind { get; set; }
    public string BaseUrl { get; set; } = "";
    public string WebPassword { get; set; } = "";
}

/// <summary>The old git_bucket block: a code_host block without a kind.</summary>
internal sealed class GitBucketFile
{
    public string BaseUrl { get; set; } = "";
    public string WebPassword { get; set; } = "";
}

internal class SettingsFile
{
    public string? TriggerLabel { get; set; }
    public string? TriggerKeyword { get; set; }
    public List<string>? Workflows { get; set; }
    public Dictionary<string, string>? Models { get; set; }
    public LimitsFile? Limits { get; set; }
    public ImplementFile? Implement { get; set; }
    public ReviewFile? Review { get; set; }
    public List<string>? NeverEdit { get; set; }
    public bool? AutoStartFirstSubIssue { get; set; }
    public bool? ClaimEnabled { get; set; }
}

internal sealed class LimitsFile
{
    public int? MaxChangedFiles { get; set; }
    public int? MaxDiffLines { get; set; }
    public int? MaxAnalysisRounds { get; set; }
    public int? MaxReviewFixes { get; set; }
    public int? MaxReviews { get; set; }
}

internal sealed class ImplementFile
{
    public int? MaxFixRounds { get; set; }
}

internal sealed class ReviewFile
{
    public string? Scope { get; set; }
    public List<string>? SkipPaths { get; set; }
}

internal sealed class CommandsFile
{
    public string? Setup { get; set; }
    public string? Build { get; set; }
    public string? Test { get; set; }
    public string? Lint { get; set; }
    public List<string>? AllowShell { get; set; }
    public string? RunIn { get; set; }
}

internal sealed class ComponentFile
{
    public string? Repo { get; set; }
    public string? Path { get; set; }
    public bool? Code { get; set; }
    public CommandsFile? Commands { get; set; }
    public List<string>? Instructions { get; set; }
}

internal sealed class ProjectFile : SettingsFile
{
    public string? IssuesIn { get; set; }
    public string? Context { get; set; }
    public CommandsFile? Commands { get; set; }
    public Dictionary<string, ComponentFile> Components { get; set; } = [];
}

/// <summary>The old flat entry: one repo, its settings become the project's overrides.</summary>
internal sealed class LegacyRepoFile : SettingsFile
{
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    public CommandsFile? Commands { get; set; }
}
