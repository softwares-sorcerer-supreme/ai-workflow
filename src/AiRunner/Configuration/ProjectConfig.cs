namespace AiRunner.Configuration;

/// <summary>
/// A group of components (repo + folder) that are worked on together. Issues and sub-issues live in one place,
/// the <see cref="IssuesIn"/> component; code is changed in one component per job. Every decision goes through
/// <see cref="ComponentConfig.Repo"/> and <see cref="ComponentConfig.Path"/>, so multi-repo, separate issues repo
/// and monorepo are the same shape.
/// </summary>
public sealed class ProjectConfig
{
    public string Name { get; set; } = "";

    /// <summary>The settings after built-in defaults, <c>defaults:</c> and this project's overrides are merged.</summary>
    public ProjectSettings Settings { get; set; } = new();

    public List<ComponentConfig> Components { get; set; } = [];

    /// <summary>Name of the component whose repo receives the issues.</summary>
    public string IssuesIn { get; set; } = "";

    /// <summary>Project description file, inside the issues component's folder.</summary>
    public string Context { get; set; } = "PROJECT.md";

    public ComponentConfig IssuesComponent =>
        Components.FirstOrDefault(component => component.Name == IssuesIn)
        ?? throw new InvalidOperationException($"Project {Name}: issues_in '{IssuesIn}' is not a component");

    public string IssuesRepo => IssuesComponent.Repo;

    public IEnumerable<ComponentConfig> CodeComponents => Components.Where(component => component.Code);

    /// <summary>The names the parsers accept in a Component column or a "Components affected" line.</summary>
    public IReadOnlyList<string> CodeComponentNames => CodeComponents.Select(component => component.Name).ToList();

    /// <summary>Every distinct repo of the project, in the order of the components.</summary>
    public IReadOnlyList<string> Repos =>
        Components
            .Select(component => component.Repo)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Repos with at least one code component: their PRs are polled.</summary>
    public IReadOnlyList<string> CodeRepos =>
        CodeComponents
            .Select(component => component.Repo)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool HasRepo(string repo) =>
        Components.Any(component => string.Equals(component.Repo, repo, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ComponentConfig> CodeComponentsIn(string repo) =>
        CodeComponents.Where(component => string.Equals(component.Repo, repo, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The only code component, or null. Data written before components existed names no component; with
    /// exactly one code component there is no doubt where the work goes.
    /// </summary>
    public ComponentConfig? SoleCodeComponent
    {
        get
        {
            var codeComponents = CodeComponents.Take(2).ToList();
            return codeComponents.Count == 1 ? codeComponents[0] : null;
        }
    }

    /// <summary>
    /// The code component a work item names, or null when the name is unknown. No name (data from before
    /// components existed) means the only code component, if there is exactly one.
    /// </summary>
    public ComponentConfig? CodeComponentOf(string? componentName)
    {
        if (componentName is null)
        {
            return SoleCodeComponent;
        }

        return CodeComponents.FirstOrDefault(component => component.Name == componentName);
    }

    /// <summary>The folder name of a repo inside the project folder: the repo name without the owner.</summary>
    public static string FolderOf(string repo)
    {
        var slash = repo.IndexOf('/');
        return slash < 0 ? repo : repo[(slash + 1)..];
    }

    public override string ToString() => Name;
}

/// <summary>A repo plus an optional folder in it. Components that share a repo share one clone.</summary>
public sealed class ComponentConfig
{
    public string Name { get; set; } = "";

    /// <summary>Full name of the repo: owner/name.</summary>
    public string Repo { get; set; } = "";

    /// <summary>Folder inside the repo, with "/" and no "..". "." is the whole repo.</summary>
    public string Path { get; set; } = ".";

    /// <summary>False = never changed and never gets a PR (an issues or docs repo).</summary>
    public bool Code { get; set; } = true;

    /// <summary>Project commands overridden by this component's own, key by key.</summary>
    public CommandsConfig Commands { get; set; } = new();

    /// <summary>
    /// The files of this component that hold the rules for the agent, relative to the component folder, with "/".
    /// The runner copies them in front of the agent on every job (AGENTS.md in the project folder).
    /// </summary>
    public List<string> Instructions { get; set; } = [DefaultInstructionFile];

    public const string DefaultInstructionFile = "AGENTS.md";

    public bool IsWholeRepo => Path == ".";

    /// <summary>Where the commands run inside a clone: the component folder, or the clone root when run_in is repo.</summary>
    public string CommandFolder(string cloneFolder) => Commands.RunsAtRepoRoot ? cloneFolder : FolderIn(cloneFolder);

    /// <summary>The component folder inside the clone of its repo.</summary>
    public string FolderIn(string cloneFolder)
    {
        if (IsWholeRepo)
        {
            return cloneFolder;
        }

        var relative = Path.Replace('/', System.IO.Path.DirectorySeparatorChar);
        return System.IO.Path.Combine(cloneFolder, relative);
    }

    public override string ToString() => Name;
}

/// <summary>Settings a project may override; the <c>defaults:</c> section of repos.yml has the same keys.</summary>
public sealed class ProjectSettings
{
    /// <summary>The runner only looks at issues with this label (or assigned to its user).</summary>
    public string TriggerLabel { get; set; } = "ai";

    /// <summary>The word that asks the bot for something in a comment.</summary>
    public string TriggerKeyword { get; set; } = "@ai";

    public List<string> Workflows { get; set; } = [.. AiRunner.Configuration.Workflows.All];
    public Dictionary<string, string> Models { get; set; } = [];
    public LimitsConfig Limits { get; set; } = new();
    public ImplementConfig Implement { get; set; } = new();
    public ReviewConfig Review { get; set; } = new();

    /// <summary>Files the agent must not change (gitignore-like patterns); such edits are undone.</summary>
    public List<string> NeverEdit { get; set; } = [];

    public bool AutoStartFirstSubIssue { get; set; }

    /// <summary>Several runners may share the project: a job claims its issue or PR first.</summary>
    public bool ClaimEnabled { get; set; }

    public bool IsEnabled(string workflow) => Workflows.Contains(workflow, StringComparer.OrdinalIgnoreCase);

    /// <summary>Model for a workflow, or null for the Copilot default.</summary>
    public string? ModelFor(string workflow) =>
        Models.TryGetValue(workflow, out var model) && !string.IsNullOrWhiteSpace(model) ? model : null;
}

/// <summary>
/// Build, test and lint commands of one component, in any language ("npm test", "mvn -q verify",
/// "dotnet test X.sln"). Copilot may run the tools of these commands with any arguments; the runner
/// runs them again after the agent stops, sends failures back for a fix round, and marks the PR WIP
/// when they still fail. Empty = not checked.
/// </summary>
public sealed class CommandsConfig
{
    public const string RunInComponent = "component";
    public const string RunInRepo = "repo";

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

    /// <summary>"component" (default) runs commands in the component folder; "repo" at the clone root.</summary>
    public string RunIn { get; set; } = RunInComponent;

    public bool RunsAtRepoRoot => string.Equals(RunIn, RunInRepo, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Automatic reviews per PR, one per new head commit (fix commits too); the first covers the whole PR, later ones
    /// only the new commits. The label <c>ai-review-full</c> asks for one more full review outside this limit.
    /// </summary>
    public int MaxReviews { get; set; } = 3;

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
    public const string ScopedValue = "scoped";
    public const string AllValue = "all";

    /// <summary>
    /// "scoped" (default) = only PRs labelled with the trigger label, plus PRs this runner opened itself from ai/* or feat/issue-*.
    /// "all" = every open PR. Scoped is the default so the bot does not comment on colleagues' PRs uninvited,
    /// and several runners do not all review the same PR.
    /// </summary>
    public string Scope { get; set; } = ScopedValue;

    /// <summary>Glob patterns; a PR that changes only these files is not reviewed.</summary>
    public List<string> SkipPaths { get; set; } = [];

    public bool IsScoped => string.Equals(Scope, ScopedValue, StringComparison.OrdinalIgnoreCase);
}
