using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiRunner.Configuration;

/// <summary>
/// One key of repos.yml as the Settings screen shows it. <see cref="Path"/> is the pattern from the file root
/// (<c>*</c> = any project or component name, <c>[]</c> = any entry of the old repos list); <see cref="Key"/> is the
/// same key inside its scope's object, so one form serves defaults and every project.
/// </summary>
/// <param name="Type"><c>string</c>, <c>integer</c>, <c>boolean</c>, <c>string-list</c> or <c>string-map</c>.</param>
public sealed record SettingDefinition(string Path, string Key, string Scope, string Type, string Help)
{
    /// <summary>The only values the resolver accepts (for a list: for each entry); empty = any.</summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = [];

    /// <summary>The built-in value when the key is not written; null = none (for a model: Copilot's default).</summary>
    public JsonNode? Default { get; init; }

    /// <summary>
    /// What the screen writes under a field the scope does not set: <c>Built-in default</c> (the screen adds the
    /// value), or <c>Not set.</c> when there is no built-in value. Sent, so the UI builds no label of its own.
    /// </summary>
    public string DefaultLabel => Default is null ? SettingsJson.NotSetLabel : SettingsJson.BuiltInLabel;

    /// <summary>
    /// Saved, but applied only at the next start (<see cref="RunnerConfigSource"/> keeps the old one).
    /// </summary>
    public bool IsRestartNeeded { get; init; }

    /// <summary>Never sent to the browser: the file shows <c>{ "isSet": true|false }</c> instead.</summary>
    public bool IsSecret { get; init; }
}

/// <summary>Where a key lives: the scopes of <see cref="SettingDefinition.Scope"/>.</summary>
public static class SettingScopes
{
    public const string Global = "global";
    public const string Defaults = "defaults";
    public const string Project = "project";
    public const string Component = "component";

    /// <summary>The old forms (<c>git_bucket:</c>, the flat <c>repos:</c> list): still read, edited by hand.</summary>
    public const string Legacy = "legacy";
}

/// <summary>
/// Every key repos.yml accepts, with its help text (the comments of today's file, which a save drops), its type,
/// allowed values and built-in default. The Settings screen is built from it, so a new key needs no change of the
/// screen;
/// <c>SettingsSchemaTests</c> fails when a key of the file types has no entry here.
/// </summary>
public static class SettingsSchema
{
    private const string Text = "string";
    private const string Number = "integer";
    private const string Flag = "boolean";
    private const string TextList = "string-list";

    private const string DefaultsPrefix = "defaults.";
    private const string ProjectPrefix = "projects.*.";
    private const string ComponentPrefix = "projects.*.components.*.";
    private const string LegacyPrefix = "repos[].";

    private static readonly JsonSerializerOptions DefaultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IReadOnlyList<SettingDefinition> All { get; } = Build();

    /// <summary>The keys a project may override; <c>defaults:</c> has the same ones.</summary>
    public static IEnumerable<SettingDefinition> ProjectSettings =>
        All.Where(entry => entry.Scope == SettingScopes.Defaults);

    private static List<SettingDefinition> Build()
    {
        var entries = new List<SettingDefinition>();
        entries.AddRange(GlobalKeys());
        entries.AddRange(Under(SettingKeys(), DefaultsPrefix, SettingScopes.Defaults));
        entries.AddRange(ProjectKeys());
        entries.AddRange(Under(SettingKeys(), ProjectPrefix, SettingScopes.Project));
        entries.AddRange(Under(CommandKeys(), ProjectPrefix, SettingScopes.Project));
        entries.AddRange(ComponentKeys());
        entries.AddRange(Under(CommandKeys(), ComponentPrefix, SettingScopes.Component));
        entries.AddRange(LegacyKeys());

        return entries;
    }

    /// <summary>The same keys placed in one scope: the path is the scope's prefix plus the key.</summary>
    private static IEnumerable<SettingDefinition> Under(
        IEnumerable<SettingDefinition> keys,
        string prefix,
        string scope) =>
        keys.Select(key => key with { Path = prefix + key.Key, Scope = scope });

    private static SettingDefinition Global(string key, string type, string help) =>
        new(key, key, SettingScopes.Global, type, help);

    /// <summary>
    /// The keys of <c>models.&lt;workflow&gt;</c>. In the file the value may also be one plain name, which means
    /// <c>default</c>; the screen edits the object form.
    /// </summary>
    private static IEnumerable<SettingDefinition> ModelKeys(string workflow)
    {
        var breakdownNote = workflow == Workflows.Analyze
            ? " The breakdown uses the analysis, so this also picks its model."
            : "";
        var canHaveFixRounds = workflow is Workflows.Implement or Workflows.Fix;

        yield return new SettingDefinition(
            $"models.{workflow}.{ModelSetting.DefaultKey}",
            $"models.{workflow}.{ModelSetting.DefaultKey}",
            "",
            Text,
            $"Copilot model of the {workflow} workflow, e.g. claude-sonnet-5.5; empty = Copilot's default." +
            breakdownNote);

        yield return new SettingDefinition(
            $"models.{workflow}.{ModelSetting.SimpleKey}",
            $"models.{workflow}.{ModelSetting.SimpleKey}",
            "",
            Text,
            $"Model of the {workflow} workflow for an issue analysed as simple (a sub-issue reads its parent).");

        yield return new SettingDefinition(
            $"models.{workflow}.{ModelSetting.ComplexKey}",
            $"models.{workflow}.{ModelSetting.ComplexKey}",
            "",
            Text,
            $"Model of the {workflow} workflow for an issue analysed as complex (a sub-issue reads its parent).");

        if (canHaveFixRounds)
        {
            yield return new SettingDefinition(
                $"models.{workflow}.{ModelSetting.FixRoundsKey}",
                $"models.{workflow}.{ModelSetting.FixRoundsKey}",
                "",
                Text,
                $"Model of the Copilot runs after round 1 of the {workflow} workflow (the fix rounds).");
        }
    }

    private static JsonNode? Json(object? value) => JsonSerializer.SerializeToNode(value, DefaultJson);

    private static IEnumerable<SettingDefinition> GlobalKeys()
    {
        var file = new RunnerFile();
        var copilot = new CopilotConfig();
        var paths = new PathsConfig();
        var attachments = new AttachmentsConfig();
        var dashboard = new DashboardConfig();
        var git = new GitConfig();

        return
        [
            Global(
                "code_host.kind",
                Text,
                "Which provider talks to the code host. The only one so far: gitbucket.") with
            {
                AllowedValues = CodeHostKinds.All, IsRestartNeeded = true,
            },
            Global("code_host.base_url", Text, "Address of the code host, e.g. http://10.221.10.34:3000.") with
            {
                IsRestartNeeded = true,
            },
            Global(
                "code_host.web_password",
                Text,
                "Sign-in password of the token's user: closes issues through the web page (the API cannot). " +
                "Kept for old setups; the Setup page is preferred (a saved one wins over this one).") with
            {
                IsSecret = true, IsRestartNeeded = true,
            },
            Global(
                "allowed_models",
                TextList,
                "When not empty, every model in models, agent_models, an agent file and an issue override must be " +
                "one of these. Empty = no check.") with
            {
                Default = Json(file.AllowedModels),
            },
            Global(
                "agent_models",
                "string-map",
                "Model per subagent, e.g. code-scout: claude-haiku-5.5. Written into the installed copy under " +
                "~/.copilot/agents (the file in this repo is not changed); one folder serves every project.") with
            {
                Default = Json(file.AgentModels),
            },
            Global(
                "poll_interval_seconds",
                Number,
                "Seconds between two polls of the code host.") with
            {
                Default = Json(file.PollIntervalSeconds),
            },
            Global(
                "runner_name",
                Text,
                "Names this runner in claims (claim_enabled); empty = the machine user name.") with
            {
                Default = Json(file.RunnerName), IsRestartNeeded = true,
            },
            Global("copilot.analyze_timeout_minutes", Number, "Time limit of one analysis run.") with
            {
                Default = Json(copilot.AnalyzeTimeoutMinutes),
            },
            Global("copilot.review_timeout_minutes", Number, "Time limit of one review run.") with
            {
                Default = Json(copilot.ReviewTimeoutMinutes),
            },
            Global(
                "copilot.implement_timeout_minutes",
                Number,
                "Time limit per Copilot run of an implementation; the agent builds, tests, lints and reviews itself " +
                "inside it.") with
            {
                Default = Json(copilot.ImplementTimeoutMinutes),
            },
            Global(
                "copilot.extra_args",
                TextList,
                "Flags for every Copilot run, e.g. --disable-builtin-mcps (no GitHub MCP server). The runner's " +
                "--deny-tool rules (git, .ai-workflow, analyze/review write and shell) still win.") with
            {
                Default = Json(copilot.ExtraArgs),
            },
            Global(
                "copilot.command",
                Text,
                "The Copilot CLI to start, e.g. copilot or the full path of copilot.cmd.") with
            {
                Default = Json(copilot.Command),
            },
            Global(
                "paths.work",
                Text,
                "work/<project>/<repo>: one clone per repo, reset before each job. Relative to repos.yml.") with
            {
                Default = Json(paths.Work), IsRestartNeeded = true,
            },
            Global(
                "paths.logs",
                Text,
                "logs/jobs/<run>/ (kept log_retention_days) and logs/cost.csv. Relative to repos.yml.") with
            {
                Default = Json(paths.Logs), IsRestartNeeded = true,
            },
            Global("paths.state", Text, "The SQLite file; deleting it is safe. Relative to repos.yml.") with
            {
                Default = Json(paths.State), IsRestartNeeded = true,
            },
            Global("log_retention_days", Number, "Days the job logs and the job history are kept.") with
            {
                Default = Json(file.LogRetentionDays),
            },
            Global(
                "attachments.enabled",
                Flag,
                "Download the files people attach to issues, comments and PRs into .ai-workflow/attachments/ of " +
                "the project folder, so the agent can read them.") with
            {
                Default = Json(attachments.Enabled),
            },
            Global(
                "attachments.max_file_bytes",
                Number,
                "Per file; larger files are listed as \"too large\", never kept.") with
            {
                Default = Json(attachments.MaxFileBytes),
            },
            Global("attachments.max_files", Number, "Files per job.") with { Default = Json(attachments.MaxFiles) },
            Global("attachments.max_total_bytes", Number, "Bytes per job.") with
            {
                Default = Json(attachments.MaxTotalBytes),
            },
            Global(
                "attachments.pass_to_copilot",
                Flag,
                "Also give images and PDFs to the prompt (copilot --attachment); needs a Copilot CLI that has " +
                "the flag.") with
            {
                Default = Json(attachments.PassToCopilot),
            },
            Global("attachments.max_passed_to_copilot", Number, "Each file on the prompt costs tokens.") with
            {
                Default = Json(attachments.MaxPassedToCopilot),
            },
            Global("attachments.skip_extensions", TextList, "Never downloaded.") with
            {
                Default = Json(attachments.SkipExtensions),
            },
            Global(
                "dashboard.enabled",
                Flag,
                "Serve this dashboard. Only the polling loop serves it (never --once or --dry-run).") with
            {
                Default = Json(dashboard.Enabled), IsRestartNeeded = true,
            },
            Global("dashboard.port", Number, "http://localhost:<port>/, 127.0.0.1 only.") with
            {
                Default = Json(dashboard.Port), IsRestartNeeded = true,
            },
            Global(
                "git.user_name",
                Text,
                "Author name of the commits the runner makes.") with
            {
                Default = Json(git.UserName),
            },
            Global("git.user_email", Text, "Author e-mail of the commits the runner makes.") with
            {
                Default = Json(git.UserEmail),
            },
        ];
    }

    /// <summary>The keys of <c>defaults:</c>, which a project overrides (objects key by key, lists replace).</summary>
    private static IEnumerable<SettingDefinition> SettingKeys()
    {
        var settings = new ProjectSettings();
        var limits = settings.Limits;

        SettingDefinition Setting(string key, string type, string help) => new("", key, "", type, help);

        var models = Workflows.WithModel.SelectMany(ModelKeys);

        return
        [
            Setting(
                "trigger_label",
                Text,
                "The runner only looks at issues with this label (or assigned to the runner's user).") with
            {
                Default = Json(settings.TriggerLabel),
            },
            Setting("trigger_keyword", Text, "The word that asks the bot for something in a comment.") with
            {
                Default = Json(settings.TriggerKeyword),
            },
            Setting(
                "workflows",
                TextList,
                "analyze = Workflow 1, breakdown = 1b, implement = 2, review = 3, fix = 3b (the keyword on a PR: " +
                "Copilot fixes it and pushes a commit when build/test pass; needs commands.build or " +
                "commands.test).") with
            {
                AllowedValues = Workflows.All, Default = Json(settings.Workflows),
            },
            .. models,
            Setting(
                "limits.max_changed_files",
                Number,
                "Above this many changed files the implementation opens no PR.") with
            {
                Default = Json(limits.MaxChangedFiles),
            },
            Setting(
                "limits.max_diff_lines",
                Number,
                "Above this many changed lines the implementation opens no PR.") with
            {
                Default = Json(limits.MaxDiffLines),
            },
            Setting(
                "limits.max_analysis_rounds",
                Number,
                "Analysis rounds per issue (each answer to the questions is one).") with
            {
                Default = Json(limits.MaxAnalysisRounds),
            },
            Setting(
                "limits.max_review_fixes",
                Number,
                "Review fixes per PR; each is one or more Copilot runs (fix rounds as in implement).") with
            {
                Default = Json(limits.MaxReviewFixes),
            },
            Setting(
                "limits.max_reviews",
                Number,
                "Automatic reviews per PR (the first is full, later ones only the new commits); the label " +
                "ai-review-full asks for one more full review outside this limit. 1 or more.") with
            {
                Default = Json(limits.MaxReviews),
            },
            Setting(
                "implement.max_fix_rounds",
                Number,
                "After the agent stops, the runner re-runs build/test/lint and checks PR_BODY.md; on a problem it " +
                "starts up to this many extra Copilot runs. 0 = WIP PR at once. A round that ends with the same " +
                "failing checks as the one before stops early. The review fix uses the same number.") with
            {
                Default = Json(settings.Implement.MaxFixRounds),
            },
            Setting(
                "review.scope",
                Text,
                "scoped = PRs labelled with trigger_label + PRs this runner opened from ai/* or feat/issue-*; " +
                "all = every open PR.") with
            {
                AllowedValues = [ReviewConfig.ScopedValue, ReviewConfig.AllValue],
                Default = Json(settings.Review.Scope),
            },
            Setting(
                "review.skip_paths",
                TextList,
                "A PR that changes only these files is not reviewed (glob patterns).") with
            {
                Default = Json(settings.Review.SkipPaths),
            },
            Setting(
                "never_edit",
                TextList,
                "Files the agent must not change (gitignore-like); such edits are undone. " +
                "e.g. migrations/, Jenkinsfile.") with
            {
                Default = Json(settings.NeverEdit),
            },
            Setting(
                "auto_start_first_sub_issue",
                Flag,
                "true = the breakdown adds ai-implement on the first sub-issue.") with
            {
                Default = Json(settings.AutoStartFirstSubIssue),
            },
            Setting(
                "claim_enabled",
                Flag,
                "true = several runners share the projects; a job claims its issue or PR first.") with
            {
                Default = Json(settings.ClaimEnabled),
            },
        ];
    }

    private static IEnumerable<SettingDefinition> ProjectKeys() =>
    [
        new SettingDefinition(ProjectPrefix + "issues_in", "issues_in", SettingScopes.Project, Text,
            "The component whose repo receives the issues and sub-issues. With one component it may be left out."),
        new SettingDefinition(ProjectPrefix + "context", "context", SettingScopes.Project, Text,
            "Optional description of the project, inside the issues component.") with
        {
            Default = Json("PROJECT.md"),
        },
    ];

    /// <summary>
    /// The commands of a component (or of the project, which its components override key by key). Any stack: the same
    /// commands as in the repo's AGENTS.md; empty = not used.
    /// </summary>
    private static IEnumerable<SettingDefinition> CommandKeys()
    {
        var commands = new CommandsConfig();

        SettingDefinition Command(string key, string type, string help) =>
            new("", "commands." + key, "", type, help);

        return
        [
            Command(
                "setup",
                Text,
                "Runs after each checkout (the work folder is cleaned, so node_modules etc. are gone), e.g. npm ci."),
            Command(
                "build",
                Text,
                "e.g. dotnet build X.sln or npm run build. The agent may run its tool with any arguments; never git."),
            Command("test", Text, "e.g. npm test."),
            Command("lint", Text, "Optional linter or format check, e.g. dotnet format --verify-no-changes."),
            Command(
                "allow_shell",
                TextList,
                "Extra Copilot shell permission patterns, e.g. npm:* or Get-ChildItem:*.") with
            {
                Default = Json(commands.AllowShell),
            },
            Command("run_in", Text, "component = in the component folder; repo = at the root of the clone.") with
            {
                AllowedValues = [CommandsConfig.RunInComponent, CommandsConfig.RunInRepo],
                Default = Json(commands.RunIn),
            },
        ];
    }

    private static IEnumerable<SettingDefinition> ComponentKeys() =>
    [
        new SettingDefinition(
            ComponentPrefix + "repo",
            "repo",
            SettingScopes.Component,
            Text,
            "The repo of the component: owner/name."),
        new SettingDefinition(ComponentPrefix + "path", "path", SettingScopes.Component, Text,
            "Folder inside the repo, with / and no ..; . is the whole repo.") with
        {
            Default = Json("."),
        },
        new SettingDefinition(ComponentPrefix + "code", "code", SettingScopes.Component, Flag,
            "false = holds issues and docs only: never changed, never gets a PR.") with
        {
            Default = Json(true),
        },
        new SettingDefinition(ComponentPrefix + "instructions", "instructions", SettingScopes.Component, TextList,
            "Files whose content the runner puts in front of the agent, relative to the component folder. " +
            "Each file costs context on every job of this component.") with
        {
            Default = Json(new[] { ComponentConfig.DefaultInstructionFile }),
        },
    ];

    private static IEnumerable<SettingDefinition> LegacyKeys()
    {
        const string LegacyHelp = " (old form: move it to code_host / projects when you can)";

        List<SettingDefinition> entries =
        [
            new SettingDefinition(
                "git_bucket.base_url",
                "git_bucket.base_url",
                SettingScopes.Legacy,
                Text,
                "Address of GitBucket" + LegacyHelp) with
            {
                IsRestartNeeded = true,
            },
            new SettingDefinition("git_bucket.web_password", "git_bucket.web_password", SettingScopes.Legacy, Text,
                "Sign-in password of the token's user" + LegacyHelp) with
            {
                IsSecret = true, IsRestartNeeded = true,
            },
            new SettingDefinition(
                LegacyPrefix + "owner",
                "owner",
                SettingScopes.Legacy,
                Text,
                "Owner of the repo" + LegacyHelp),
            new SettingDefinition(
                LegacyPrefix + "name",
                "name",
                SettingScopes.Legacy,
                Text,
                "Name of the repo; it also names the project" + LegacyHelp),
        ];

        var settings = SettingKeys().Concat(CommandKeys());
        entries.AddRange(Under(settings, LegacyPrefix, SettingScopes.Legacy));

        return entries;
    }
}
