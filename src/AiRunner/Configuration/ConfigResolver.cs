using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiRunner.Configuration;

/// <summary>
/// Turns the text of repos.yml into a <see cref="RunnerConfig"/>: reads it strictly (an unknown key is an
/// error, with a hint), merges built-in defaults, <c>defaults:</c> and each project's overrides, maps the old
/// flat <c>repos:</c> list to one-component projects and checks the result. A mistake stops the start with a
/// message that names the key or project, so a typo never silently falls back to a default.
/// </summary>
internal static partial class ConfigResolver
{
    private static readonly IDeserializer Reader = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithTypeConverter(new ModelSetting.YamlConverter())
        .Build();

    /// <summary>Keys that moved: the hint says where they live now.</summary>
    private static readonly Dictionary<string, string> MovedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scope_label"] = "it is now 'trigger_label' under 'defaults' (or a project)",
    };

    [GeneratedRegex(@"Property '(?<key>[^']+)' not found on type '(?<type>[^']+)'")]
    private static partial Regex UnknownPropertyPattern();

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[^/\s]+/[^/\s]+$")]
    private static partial Regex RepoPattern();

    public static RunnerFile Deserialize(string yaml)
    {
        try
        {
            return Reader.Deserialize<RunnerFile>(yaml) ?? new RunnerFile();
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException(Describe(exception), exception);
        }
    }

    /// <summary>The config, and beside it the web password, which must stay out of it (<see cref="ConfigFile"/>).</summary>
    public static ConfigFile Resolve(RunnerFile file)
    {
        var (codeHost, webPassword) = ResolveCodeHost(file);
        var config = new RunnerConfig
        {
            CodeHost = codeHost,
            PollIntervalSeconds = file.PollIntervalSeconds,
            RunnerName = file.RunnerName,
            Copilot = file.Copilot,
            Paths = file.Paths,
            LogRetentionDays = file.LogRetentionDays,
            Attachments = file.Attachments,
            Git = file.Git,
            Dashboard = file.Dashboard,
            AllowedModels = [.. file.AllowedModels],
            AgentModels = new Dictionary<string, string>(file.AgentModels),
        };

        foreach (var (name, projectFile) in file.Projects)
        {
            var project = BuildProject(name, projectFile, file.Defaults);
            config.Projects.Add(project);
        }

        foreach (var legacy in file.Repos ?? [])
        {
            var project = BuildLegacyProject(legacy, file);
            config.Projects.Add(project);
        }

        Validate(config);

        return new ConfigFile(config, webPassword);
    }

    /// <summary>
    /// The code_host block, or the old git_bucket block read as kind gitbucket, with its web password apart. With
    /// neither, gitbucket with no URL: <see cref="RunnerConfig.LoadFile"/> asks for the URL, while a snippet in a
    /// test still parses.
    /// </summary>
    private static (CodeHostConfig Config, string WebPassword) ResolveCodeHost(RunnerFile file)
    {
        if (file.CodeHost is not null && file.GitBucket is not null)
        {
            throw new InvalidOperationException(
                "repos.yml has both 'code_host' and 'git_bucket'. Keep 'code_host' ('git_bucket' is its old name).");
        }

        if (file.GitBucket is { } oldBlock)
        {
            var oldConfig = new CodeHostConfig { Kind = CodeHostKinds.GitBucket, BaseUrl = oldBlock.BaseUrl };
            return (oldConfig, oldBlock.WebPassword);
        }

        if (file.CodeHost is not { } block)
        {
            return (new CodeHostConfig(), "");
        }

        var kind = block.Kind?.Trim() ?? "";
        var knownKinds = string.Join(", ", CodeHostKinds.All);
        if (kind.Length == 0)
        {
            throw new InvalidOperationException($"code_host.kind is missing. Use: {knownKinds}");
        }

        if (!CodeHostKinds.All.Contains(kind, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"code_host.kind '{kind}' is unknown. Use: {knownKinds}");
        }

        var config = new CodeHostConfig { Kind = kind, BaseUrl = block.BaseUrl };

        return (config, block.WebPassword);
    }

    private static ProjectConfig BuildProject(string name, ProjectFile file, SettingsFile defaults)
    {
        var project = new ProjectConfig
        {
            Name = name,
            Settings = MergeSettings(defaults, file),
            Context = string.IsNullOrWhiteSpace(file.Context) ? "PROJECT.md" : file.Context.Trim(),
        };

        foreach (var (componentName, componentFile) in file.Components)
        {
            var commands = MergeCommands(file.Commands, componentFile.Commands);
            project.Components.Add(new ComponentConfig
            {
                Name = componentName,
                Repo = componentFile.Repo?.Trim() ?? "",
                Path = componentFile.Path?.Trim() ?? ".",
                Code = componentFile.Code ?? true,
                Commands = commands,
                Instructions = componentFile.Instructions is { } instructions
                    ? [.. instructions.Select(entry => entry?.Trim() ?? "")]
                    : [ComponentConfig.DefaultInstructionFile],
            });
        }

        project.IssuesIn = file.IssuesIn?.Trim()
            ?? (project.Components.Count == 1 ? project.Components[0].Name : "");

        return project;
    }

    /// <summary>An old <c>repos:</c> entry: a project named after the repo with one component; its settings become overrides.</summary>
    private static ProjectConfig BuildLegacyProject(LegacyRepoFile legacy, RunnerFile file)
    {
        var name = FolderName(legacy.Name);
        var settings = MergeSettings(file.Defaults, legacy);
        var commands = MergeCommands(legacy.Commands);

        if (string.IsNullOrWhiteSpace(legacy.Owner) || string.IsNullOrWhiteSpace(legacy.Name))
        {
            throw new InvalidOperationException("repos: every entry needs 'owner' and 'name'");
        }

        var component = new ComponentConfig
        {
            Name = name,
            Repo = $"{legacy.Owner.Trim()}/{legacy.Name.Trim()}",
            Path = ".",
            Code = true,
            Commands = commands,
        };

        return new ProjectConfig
        {
            Name = name,
            Settings = settings,
            Components = [component],
            IssuesIn = name,
        };
    }

    /// <summary>Lowercase, and anything that cannot be in a folder name becomes "-".</summary>
    private static string FolderName(string repoName) =>
        Regex.Replace(repoName.Trim().ToLowerInvariant(), "[^a-z0-9_-]", "-");

    /// <summary>Built-in values, then each layer in order. Objects merge key by key; a list replaces the earlier list.</summary>
    private static ProjectSettings MergeSettings(params SettingsFile[] layers)
    {
        var settings = new ProjectSettings();

        foreach (var layer in layers)
        {
            settings.TriggerLabel = layer.TriggerLabel?.Trim() ?? settings.TriggerLabel;
            settings.TriggerKeyword = layer.TriggerKeyword?.Trim() ?? settings.TriggerKeyword;
            settings.Workflows = layer.Workflows is null ? settings.Workflows : [.. layer.Workflows];
            settings.NeverEdit = layer.NeverEdit is null ? settings.NeverEdit : [.. layer.NeverEdit];
            settings.AutoStartFirstSubIssue = layer.AutoStartFirstSubIssue ?? settings.AutoStartFirstSubIssue;
            settings.ClaimEnabled = layer.ClaimEnabled ?? settings.ClaimEnabled;

            foreach (var (workflow, model) in layer.Models ?? [])
            {
                var earlier = settings.Models.GetValueOrDefault(workflow) ?? new ModelSetting();
                settings.Models[workflow] = earlier.MergedWith(model);
            }

            var limits = layer.Limits;
            if (limits is not null)
            {
                settings.Limits.MaxChangedFiles = limits.MaxChangedFiles ?? settings.Limits.MaxChangedFiles;
                settings.Limits.MaxDiffLines = limits.MaxDiffLines ?? settings.Limits.MaxDiffLines;
                settings.Limits.MaxAnalysisRounds = limits.MaxAnalysisRounds ?? settings.Limits.MaxAnalysisRounds;
                settings.Limits.MaxReviewFixes = limits.MaxReviewFixes ?? settings.Limits.MaxReviewFixes;
                settings.Limits.MaxReviews = limits.MaxReviews ?? settings.Limits.MaxReviews;
            }

            settings.Implement.MaxFixRounds = layer.Implement?.MaxFixRounds ?? settings.Implement.MaxFixRounds;
            settings.Review.Scope = layer.Review?.Scope?.Trim() ?? settings.Review.Scope;
            settings.Review.SkipPaths = layer.Review?.SkipPaths is { } skipPaths
                ? [.. skipPaths]
                : settings.Review.SkipPaths;
        }

        return settings;
    }

    /// <summary>Project commands first, then the component's own over them, key by key (an empty string is a value).</summary>
    private static CommandsConfig MergeCommands(params CommandsFile?[] layers)
    {
        var commands = new CommandsConfig();

        foreach (var layer in layers)
        {
            if (layer is null)
            {
                continue;
            }

            commands.Setup = layer.Setup ?? commands.Setup;
            commands.Build = layer.Build ?? commands.Build;
            commands.Test = layer.Test ?? commands.Test;
            commands.Lint = layer.Lint ?? commands.Lint;
            commands.AllowShell = layer.AllowShell is null ? commands.AllowShell : [.. layer.AllowShell];
            commands.RunIn = layer.RunIn?.Trim() ?? commands.RunIn;
        }

        return commands;
    }

    private static void Validate(RunnerConfig config)
    {
        var port = config.Dashboard.Port;
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"dashboard.port {port} is not a port. Use 1 to 65535.");
        }

        foreach (var (agent, model) in config.AgentModels)
        {
            if (!ModelSetting.IsAllowed(config.AllowedModels, model))
            {
                throw new InvalidOperationException(
                    $"agent_models.{agent} names '{model}', which is not in allowed_models.");
            }
        }

        var projectNames = new HashSet<string>(StringComparer.Ordinal);
        var repoOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in config.Projects)
        {
            if (!projectNames.Add(project.Name))
            {
                throw new InvalidOperationException(
                    $"Two projects are named '{project.Name}' (a repos: entry gets the name of its repo). Rename one.");
            }

            ValidateName(project.Name, $"project '{project.Name}'");
            ValidateSettings(project, config.AllowedModels);
            ValidateComponents(project);

            foreach (var repo in project.Repos)
            {
                if (repoOwners.TryGetValue(repo, out var otherProject))
                {
                    throw new InvalidOperationException(
                        $"Repo {repo} is in projects '{otherProject}' and '{project.Name}'. " +
                        "A repo belongs to one project.");
                }

                repoOwners[repo] = project.Name;
            }
        }
    }

    private static void ValidateName(string name, string what)
    {
        if (!NamePattern().IsMatch(name))
        {
            throw new InvalidOperationException(
                $"The name of {what} may only have lowercase letters, digits, '-' and '_' (it becomes a folder name).");
        }
    }

    private static void ValidateSettings(ProjectConfig project, IReadOnlyCollection<string> allowedModels)
    {
        var settings = project.Settings;

        foreach (var unknownWorkflow in settings.Workflows.Where(name => !Workflows.All.Contains(name)))
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}': unknown workflow '{unknownWorkflow}'. " +
                $"Use: {string.Join(", ", Workflows.All)}");
        }

        foreach (var unknownModel in settings.Models.Keys.Where(name => !Workflows.WithModel.Contains(name)))
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}': models.{unknownModel} is not a workflow that runs Copilot " +
                "(the breakdown table comes from the analysis, so models.analyze picks its model). " +
                $"Use: {string.Join(", ", Workflows.WithModel)}");
        }

        foreach (var (workflow, model) in settings.Models)
        {
            var canHaveFixRounds = workflow is Workflows.Implement or Workflows.Fix;
            if (model.FixRounds is not null && !canHaveFixRounds)
            {
                throw new InvalidOperationException(
                    $"Project '{project.Name}': models.{workflow}.fix_rounds only exists for " +
                    $"{Workflows.Implement} and {Workflows.Fix} (the workflows that run Copilot again after round 1).");
            }

            foreach (var name in model.AllModels().Where(name => !ModelSetting.IsAllowed(allowedModels, name)))
            {
                throw new InvalidOperationException(
                    $"Project '{project.Name}': models.{workflow} names '{name}', which is not in allowed_models.");
            }
        }

        if (settings.Limits.MaxReviews < 1)
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}': limits.max_reviews must be 1 or more " +
                "(to stop reviews, leave review out of workflows).");
        }

        if (string.IsNullOrWhiteSpace(settings.TriggerLabel) || string.IsNullOrWhiteSpace(settings.TriggerKeyword))
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}': trigger_label and trigger_keyword may not be empty.");
        }

        var scope = settings.Review.Scope;
        if (!string.Equals(scope, ReviewConfig.ScopedValue, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(scope, ReviewConfig.AllValue, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}': review.scope '{scope}' is unknown. Use: scoped, all");
        }
    }

    private static void ValidateComponents(ProjectConfig project)
    {
        if (project.Components.Count == 0)
        {
            throw new InvalidOperationException($"Project '{project.Name}' has no components.");
        }

        foreach (var component in project.Components)
        {
            var what = $"component '{component.Name}' of project '{project.Name}'";
            ValidateName(component.Name, what);

            if (!RepoPattern().IsMatch(component.Repo))
            {
                throw new InvalidOperationException($"{what}: repo '{component.Repo}' must look like owner/name.");
            }

            ValidatePath(component, what);
            ValidateInstructions(component, what);

            var runIn = component.Commands.RunIn;
            if (!string.Equals(runIn, CommandsConfig.RunInComponent, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(runIn, CommandsConfig.RunInRepo, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{what}: commands.run_in '{runIn}' is unknown. Use: component, repo");
            }
        }

        if (string.IsNullOrWhiteSpace(project.IssuesIn))
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}': issues_in is missing. Name the component that receives the issues.");
        }

        if (project.Components.All(component => component.Name != project.IssuesIn))
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}': issues_in '{project.IssuesIn}' is not one of its components.");
        }

        if (!project.CodeComponents.Any())
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}' has no component with code: true. Nothing could be implemented.");
        }

        var clashingFolder = project.Repos
            .GroupBy(ProjectConfig.FolderOf, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (clashingFolder is not null)
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}': repos {string.Join(" and ", clashingFolder)} have the same name; " +
                "the clone folder is the repo name.");
        }

        ValidateOverlap(project);
    }

    /// <summary>A path inside a folder: not empty, written with "/", no drive, not absolute, no ".." segment.</summary>
    private static bool IsSafeRelativePath(string path)
    {
        var hasBadSegment = path.Split('/').Any(segment => segment == "..");

        return !string.IsNullOrWhiteSpace(path) &&
               !path.Contains('\\') &&
               !path.StartsWith('/') &&
               !path.Contains(':') &&
               !hasBadSegment;
    }

    private static void ValidatePath(ComponentConfig component, string what)
    {
        var path = component.Path;
        if (!IsSafeRelativePath(path))
        {
            throw new InvalidOperationException(
                $"{what}: path '{path}' must be relative, use '/' and have no '..' ('.' is the whole repo).");
        }

        var trimmed = path.TrimEnd('/');
        component.Path = trimmed.StartsWith("./", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
        if (component.Path.Length == 0)
        {
            component.Path = ".";
        }
    }

    private static void ValidateInstructions(ComponentConfig component, string what)
    {
        foreach (var entry in component.Instructions)
        {
            if (!IsSafeRelativePath(entry))
            {
                throw new InvalidOperationException(
                    $"{what}: instructions entry '{entry}' must be a file path relative to the component folder, " +
                    "use '/' and have no '..'.");
            }
        }
    }

    /// <summary>Two code components in one repo may not change the same files; a code: false one may overlap.</summary>
    private static void ValidateOverlap(ProjectConfig project)
    {
        var codeComponents = project.CodeComponents.ToList();

        for (var first = 0; first < codeComponents.Count; first++)
        {
            for (var second = first + 1; second < codeComponents.Count; second++)
            {
                var left = codeComponents[first];
                var right = codeComponents[second];
                var isSameRepo = string.Equals(left.Repo, right.Repo, StringComparison.OrdinalIgnoreCase);
                if (isSameRepo && PathsOverlap(left.Path, right.Path))
                {
                    throw new InvalidOperationException(
                        $"Project '{project.Name}': components '{left.Name}' and '{right.Name}' " +
                        $"overlap in {left.Repo}.");
                }
            }
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        if (left == "." || right == ".")
        {
            return true;
        }

        return IsInside(left, right) || IsInside(right, left);
    }

    private static bool IsInside(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>"unknown key 'maxDiffLines' at line 12 (did you mean 'max_diff_lines'?)", or the YAML message as it is.</summary>
    private static string Describe(YamlException exception)
    {
        var match = UnknownPropertyPattern().Match(exception.Message);
        var line = exception.Start.Line;

        if (!match.Success)
        {
            var inner = exception.InnerException?.Message ?? exception.Message;
            return $"repos.yml line {line}: {inner}";
        }

        var key = match.Groups["key"].Value;
        var type = typeof(RunnerFile).Assembly.GetType(match.Groups["type"].Value);
        var hint = MovedKeys.TryGetValue(key, out var moved)
            ? $" ({moved})"
            : Suggest(key, type) is { } suggestion ? $" (did you mean '{suggestion}'?)" : "";

        return $"repos.yml line {line}: unknown key '{key}'{hint}";
    }

    private static string? Suggest(string key, Type? type)
    {
        if (type is null)
        {
            return null;
        }

        var known = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && !property.IsDefined(typeof(YamlIgnoreAttribute)))
            .Select(property => UnderscoredNamingConvention.Instance.Apply(property.Name))
            .ToList();

        var asSnakeCase = UnderscoredNamingConvention.Instance.Apply(key);
        if (known.Contains(asSnakeCase))
        {
            return asSnakeCase;
        }

        return known
            .Select(candidate => (Candidate: candidate, Distance: EditDistance(key.ToLowerInvariant(), candidate)))
            .Where(entry => entry.Distance <= 2)
            .OrderBy(entry => entry.Distance)
            .Select(entry => entry.Candidate)
            .FirstOrDefault();
    }

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();

        for (var row = 1; row <= left.Length; row++)
        {
            var current = new int[right.Length + 1];
            current[0] = row;

            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            previous = current;
        }

        return previous[right.Length];
    }
}
