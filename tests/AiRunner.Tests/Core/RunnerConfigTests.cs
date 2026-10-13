using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

/// <summary>repos.yml through <see cref="RunnerConfig.Parse"/>: snake_case, defaults, projects, the old flat list, errors.</summary>
public class RunnerConfigTests
{
    private const string ThreeComponents = """
        projects:
          mbh:
            issues_in: issues
            components:
              issues:   { repo: org/mbh-issues, code: false }
              backend:  { repo: org/mbh-backend, commands: { build: dotnet build } }
              frontend: { repo: org/mbh-frontend, commands: { build: npm run build } }
        """;

    private static InvalidOperationException Fails(string yaml) =>
        Assert.Throws<InvalidOperationException>(() => RunnerConfig.Parse(yaml));

    [Fact]
    public void Old_flat_repos_list_becomes_one_project_with_one_component()
    {
        var config = RunnerConfig.Parse("""
            git_bucket:
              base_url: http://gitbucket
            repos:
              - owner: o
                name: r
                workflows: [analyze, implement]
                models: { analyze: big-model }
                commands:
                  build: npm run build
                  test: npm test
                review: { scope: scoped, skip_paths: ["**/*.md"] }
            """);

        var project = config.Projects.Single();
        var component = project.Components.Single();
        var settings = project.Settings;

        Assert.Equal("r", project.Name);
        Assert.Equal("r", project.IssuesIn);
        Assert.Equal("o/r", component.Repo);
        Assert.Equal(".", component.Path);
        Assert.True(component.Code);
        Assert.Same(project, config.FindProject("o/r"));
        Assert.True(settings.IsEnabled("analyze"));
        Assert.False(settings.IsEnabled("review"));
        Assert.Equal("big-model", settings.ModelFor("analyze"));
        Assert.Null(settings.ModelFor("implement"));
        Assert.Equal(["npm run build", "npm test"], component.Commands.Verification());
        Assert.True(settings.Review.IsScoped);
        Assert.Equal(["**/*.md"], settings.Review.SkipPaths);
        Assert.Equal(15, settings.Limits.MaxChangedFiles);
        Assert.Equal(90, config.PollIntervalSeconds);
    }

    [Fact]
    public void A_project_has_named_components_with_issues_in_one_of_them()
    {
        var config = RunnerConfig.Parse(ThreeComponents);

        var project = config.Projects.Single();

        Assert.Equal("mbh", project.Name);
        Assert.Equal("org/mbh-issues", project.IssuesRepo);
        Assert.Equal(["backend", "frontend"], project.CodeComponents.Select(component => component.Name));
        Assert.Equal(["org/mbh-issues", "org/mbh-backend", "org/mbh-frontend"], project.Repos);
        Assert.Equal(["org/mbh-backend", "org/mbh-frontend"], project.CodeRepos);
        Assert.Null(project.SoleCodeComponent);
        Assert.Same(project, config.FindProject("ORG/mbh-frontend"));
        Assert.Null(config.FindProject("org/other"));
        Assert.Equal("PROJECT.md", project.Context);
    }

    [Fact]
    public void Components_of_a_monorepo_share_one_repo()
    {
        var config = RunnerConfig.Parse("""
            projects:
              mono:
                issues_in: web
                components:
                  web: { repo: org/mono, path: apps/web }
                  api: { repo: org/mono, path: apps/api/ }
            """);

        var project = config.Projects.Single();

        Assert.Equal(["org/mono"], project.Repos);
        Assert.Equal("apps/api", project.Components[1].Path);   // the trailing slash is dropped
        Assert.Equal(2, project.CodeComponentsIn("org/mono").Count());
    }

    [Fact]
    public void Defaults_fill_every_project_and_a_project_overrides_only_what_it_names()
    {
        var config = RunnerConfig.Parse("""
            defaults:
              trigger_label: bot
              trigger_keyword: "@bot"
              workflows: [analyze, review]
              models: { analyze: strong, implement: cheap }
              limits: { max_diff_lines: 500, max_changed_files: 9 }
              review: { skip_paths: ["docs/**"] }
              never_edit: [Jenkinsfile]
            projects:
              plain:
                issues_in: app
                components:
                  app: { repo: org/plain }
              tuned:
                issues_in: app
                trigger_label: ai
                workflows: [implement]
                models: { implement: other }
                limits: { max_diff_lines: 400 }
                components:
                  app: { repo: org/tuned }
            """);

        var plain = config.Projects[0].Settings;
        var tuned = config.Projects[1].Settings;

        Assert.Equal("bot", plain.TriggerLabel);
        Assert.Equal("@bot", plain.TriggerKeyword);
        Assert.Equal(["analyze", "review"], plain.Workflows);
        Assert.Equal(500, plain.Limits.MaxDiffLines);
        Assert.Equal(["Jenkinsfile"], plain.NeverEdit);

        // Objects merge key by key, lists replace the default list.
        Assert.Equal("ai", tuned.TriggerLabel);
        Assert.Equal("@bot", tuned.TriggerKeyword);
        Assert.Equal(["implement"], tuned.Workflows);
        Assert.Equal(400, tuned.Limits.MaxDiffLines);
        Assert.Equal(9, tuned.Limits.MaxChangedFiles);
        Assert.Equal("strong", tuned.ModelFor("analyze"));
        Assert.Equal("other", tuned.ModelFor("implement"));
        Assert.Equal(["docs/**"], tuned.Review.SkipPaths);
    }

    [Fact]
    public void Built_in_defaults_apply_when_nothing_is_written()
    {
        var settings = RunnerConfig.Parse(ThreeComponents).Projects.Single().Settings;

        Assert.Equal("ai", settings.TriggerLabel);
        Assert.Equal("@ai", settings.TriggerKeyword);
        Assert.Equal(Workflows.All.Order(), settings.Workflows.Order());
        Assert.Equal(15, settings.Limits.MaxChangedFiles);
        Assert.Equal(800, settings.Limits.MaxDiffLines);
        Assert.Equal(2, settings.Implement.MaxFixRounds);
        Assert.True(settings.Review.IsScoped);
        Assert.False(settings.ClaimEnabled);
        Assert.False(settings.AutoStartFirstSubIssue);
    }

    [Fact]
    public void Component_commands_override_project_commands_key_by_key()
    {
        var config = RunnerConfig.Parse("""
            projects:
              p:
                issues_in: a
                commands: { setup: npm ci, build: npm run build, test: npm test, run_in: repo }
                components:
                  a: { repo: o/a }
                  b: { repo: o/b, commands: { setup: "", test: npm run unit, allow_shell: ["npm:*"] } }
            """);

        var inherited = config.Projects.Single().Components[0].Commands;
        var overridden = config.Projects.Single().Components[1].Commands;

        Assert.Equal("npm ci", inherited.Setup);
        Assert.Equal("npm run build", inherited.Build);
        Assert.True(inherited.RunsAtRepoRoot);
        Assert.Equal("", overridden.Setup);                      // an empty string is a value
        Assert.Equal("npm run build", overridden.Build);          // not written: from the project
        Assert.Equal("npm run unit", overridden.Test);
        Assert.Equal(["npm:*"], overridden.AllowShell);
        Assert.True(overridden.RunsAtRepoRoot);
    }

    [Fact]
    public void Commands_run_in_the_component_folder_unless_run_in_is_repo()
    {
        var monorepo = TestProjects.Monorepo();
        var web = monorepo.Components[0];
        var clone = Path.Combine("work", "mono", "mono");

        Assert.Equal(Path.Combine(clone, "apps", "web"), web.CommandFolder(clone));

        web.Commands.RunIn = CommandsConfig.RunInRepo;

        Assert.Equal(clone, web.CommandFolder(clone));
        Assert.Equal(clone, new ComponentConfig { Repo = "o/r" }.CommandFolder(clone));
    }

    [Fact]
    public void A_single_component_project_needs_no_issues_in()
    {
        var config = RunnerConfig.Parse("""
            projects:
              solo:
                components:
                  app: { repo: o/app }
            """);

        Assert.Equal("app", config.Projects.Single().IssuesIn);
    }

    [Fact]
    public void The_runner_name_defaults_to_the_machine_user()
    {
        Assert.Equal(Environment.UserName, new RunnerConfig().EffectiveRunnerName());
        Assert.Equal("laptop-1", RunnerConfig.Parse("runner_name: laptop-1").EffectiveRunnerName());
    }

    [Fact]
    public void An_unknown_key_fails_with_its_line_and_a_hint()
    {
        var exception = Fails("""
            defaults:
              limits:
                maxDiffLines: 5
            """);

        Assert.Contains("unknown key 'maxDiffLines'", exception.Message);
        Assert.Contains("did you mean 'max_diff_lines'", exception.Message);
        Assert.Contains("line 3", exception.Message);
    }

    [Fact]
    public void A_misspelled_key_gets_the_closest_known_name()
    {
        var exception = Fails("""
            projects:
              p:
                issues_in: a
                components:
                  a: { repo: o/a, comands: { build: x } }
            """);

        Assert.Contains("unknown key 'comands'", exception.Message);
        Assert.Contains("did you mean 'commands'", exception.Message);
    }

    [Fact]
    public void Camel_case_keys_are_not_read()
    {
        var exception = Fails("pollIntervalSeconds: 5");

        Assert.Contains("did you mean 'poll_interval_seconds'", exception.Message);
    }

    [Fact]
    public void The_old_scope_label_key_points_to_trigger_label()
    {
        var exception = Fails("scope_label: ai");

        Assert.Contains("trigger_label", exception.Message);
    }

    [Fact]
    public void Commands_are_not_allowed_in_defaults()
    {
        var exception = Fails("""
            defaults:
              commands: { build: make }
            """);

        Assert.Contains("unknown key 'commands'", exception.Message);
    }

    [Fact]
    public void A_repo_may_belong_to_one_project_only()
    {
        var exception = Fails("""
            projects:
              one: { issues_in: a, components: { a: { repo: o/shared } } }
              two: { issues_in: b, components: { b: { repo: O/Shared } } }
            """);

        Assert.Contains("o/shared", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one", exception.Message);
        Assert.Contains("two", exception.Message);
    }

    [Fact]
    public void A_legacy_repo_cannot_be_in_a_project_too()
    {
        var exception = Fails("""
            projects:
              p: { issues_in: a, components: { a: { repo: o/r } } }
            repos:
              - { owner: o, name: r }
            """);

        Assert.Contains("o/r", exception.Message);
    }

    [Fact]
    public void A_project_name_cannot_collide_with_a_legacy_entry()
    {
        var exception = Fails("""
            projects:
              r: { issues_in: a, components: { a: { repo: o/other } } }
            repos:
              - { owner: o, name: r }
            """);

        Assert.Contains("named 'r'", exception.Message);
    }

    [Fact]
    public void Issues_in_must_name_a_component()
    {
        var exception = Fails("""
            projects:
              p:
                issues_in: nope
                components:
                  a: { repo: o/a }
                  b: { repo: o/b }
            """);

        Assert.Contains("issues_in 'nope'", exception.Message);
    }

    [Fact]
    public void Issues_in_is_required_when_there_is_a_choice()
    {
        var exception = Fails("""
            projects:
              p:
                components:
                  a: { repo: o/a }
                  b: { repo: o/b }
            """);

        Assert.Contains("issues_in is missing", exception.Message);
    }

    [Fact]
    public void A_project_needs_a_code_component()
    {
        var exception = Fails("""
            projects:
              p:
                issues_in: a
                components:
                  a: { repo: o/a, code: false }
            """);

        Assert.Contains("code: true", exception.Message);
    }

    [Theory]
    [InlineData("Backend")]
    [InlineData("back end")]
    [InlineData("a/b")]
    public void Component_names_become_folder_names(string name)
    {
        var yaml = $$"""
            projects:
              p:
                issues_in: "{{name}}"
                components:
                  "{{name}}": { repo: o/a }
            """;

        var exception = Fails(yaml);

        Assert.Contains("lowercase letters", exception.Message);
    }

    [Fact]
    public void Project_names_become_folder_names()
    {
        var exception = Fails("""
            projects:
              My Project: { issues_in: a, components: { a: { repo: o/a } } }
            """);

        Assert.Contains("lowercase letters", exception.Message);
    }

    [Fact]
    public void Two_repos_with_the_same_name_in_one_project_would_share_a_folder()
    {
        var exception = Fails("""
            projects:
              p:
                issues_in: a
                components:
                  a: { repo: one/app }
                  b: { repo: two/app }
            """);

        Assert.Contains("same name", exception.Message);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("/abs")]
    [InlineData("a\\b")]
    [InlineData("a/../b")]
    [InlineData("C:/x")]
    public void A_path_is_relative_with_slashes_and_no_dots_dots(string path)
    {
        var yaml = $$"""
            projects:
              p:
                issues_in: a
                components:
                  a: { repo: o/a, path: "{{path.Replace("\\", "\\\\")}}" }
            """;

        var exception = Fails(yaml);

        Assert.Contains("must be relative", exception.Message);
    }

    [Theory]
    [InlineData(".", "apps/web")]
    [InlineData("apps", "apps/web")]
    [InlineData("apps/web", "apps/web")]
    public void Code_components_in_one_repo_may_not_overlap(string first, string second)
    {
        var yaml = $$"""
            projects:
              p:
                issues_in: a
                components:
                  a: { repo: o/mono, path: {{first}} }
                  b: { repo: o/mono, path: {{second}} }
            """;

        var exception = Fails(yaml);

        Assert.Contains("overlap", exception.Message);
    }

    [Fact]
    public void A_component_without_code_may_overlap_a_code_component()
    {
        var config = RunnerConfig.Parse("""
            projects:
              p:
                issues_in: docs
                components:
                  docs: { repo: o/mono, path: ., code: false }
                  web: { repo: o/mono, path: apps/web }
            """);

        Assert.Equal(2, config.Projects.Single().Components.Count);
    }

    [Fact]
    public void Siblings_with_a_common_prefix_do_not_overlap()
    {
        var config = RunnerConfig.Parse("""
            projects:
              p:
                issues_in: a
                components:
                  a: { repo: o/mono, path: apps/web }
                  b: { repo: o/mono, path: apps/web-admin }
            """);

        Assert.Equal(2, config.Projects.Single().Components.Count);
    }

    [Fact]
    public void Unknown_workflows_and_run_in_values_and_scopes_are_errors()
    {
        var workflow = Fails("""
            defaults: { workflows: [analyze, deploy] }
            repos:
              - { owner: o, name: r }
            """);
        var runIn = Fails("""
            projects:
              p: { issues_in: a, components: { a: { repo: o/a, commands: { run_in: sideways } } } }
            """);
        var scope = Fails("""
            defaults: { review: { scope: everything } }
            repos:
              - { owner: o, name: r }
            """);
        var model = Fails("""
            defaults: { models: { deploy: x } }
            repos:
              - { owner: o, name: r }
            """);

        Assert.Contains("unknown workflow 'deploy'", workflow.Message);
        Assert.Contains("run_in 'sideways'", runIn.Message);
        Assert.Contains("review.scope 'everything'", scope.Message);
        Assert.Contains("models.deploy", model.Message);
    }

    [Fact]
    public void A_legacy_entry_needs_owner_and_name_and_a_repo_looks_like_owner_slash_name()
    {
        var legacy = Fails("""
            repos:
              - { owner: o }
            """);
        var repo = Fails("""
            projects:
              p: { issues_in: a, components: { a: { repo: just-a-name } } }
            """);

        Assert.Contains("owner", legacy.Message);
        Assert.Contains("owner/name", repo.Message);
    }

    [Fact]
    public void The_base_url_is_checked_when_the_file_is_loaded()
    {
        var file = Path.Combine(Path.GetTempPath(), $"repos-{Guid.NewGuid():N}.yml");
        File.WriteAllText(file, "poll_interval_seconds: 5");

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => RunnerConfig.Load(file));

            Assert.Contains("git_bucket.base_url", exception.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_real_repos_yml_of_this_repo_loads()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "repos.yml")))
        {
            folder = folder.Parent;
        }

        Assert.NotNull(folder);
        var config = RunnerConfig.Load(Path.Combine(folder.FullName, "repos.yml"));

        Assert.NotEmpty(config.Projects);
        Assert.All(config.Projects, project => Assert.NotEmpty(project.CodeComponents));
    }
}
