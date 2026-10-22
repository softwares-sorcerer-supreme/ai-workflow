using AiRunner.Configuration;

namespace AiRunner.Tests.Core;

/// <summary>models.&lt;workflow&gt; as a string or an object, allowed_models and agent_models in repos.yml.</summary>
public sealed class ModelConfigTests
{
    private const string Projects = """
        projects:
          shop:
            components:
              web: { repo: o/shop }
        """;

    private static InvalidOperationException Fails(string yaml) =>
        Assert.Throws<InvalidOperationException>(() => RunnerConfig.Parse(yaml));

    [Fact]
    public void The_string_form_is_the_default_model()
    {
        var config = RunnerConfig.Parse("defaults:\n  models: { implement: m-one }\n" + Projects);

        var settings = config.Projects.Single().Settings;

        Assert.Equal("m-one", settings.ModelSettingFor("implement")!.Default);
        Assert.Null(settings.ModelSettingFor("implement")!.Complex);
        Assert.Null(settings.ModelSettingFor("review"));
    }

    [Fact]
    public void The_object_form_reads_every_key()
    {
        var config = RunnerConfig.Parse("""
            defaults:
              models:
                implement: { default: m-d, simple: m-s, complex: m-c, fix_rounds: m-f }
            projects:
              shop:
                components:
                  web: { repo: o/shop }
            """);

        var setting = config.Projects.Single().Settings.ModelSettingFor("implement");

        Assert.Equal(new ModelSetting { Default = "m-d", Simple = "m-s", Complex = "m-c", FixRounds = "m-f" }, setting);
    }

    [Fact]
    public void A_project_overrides_one_key_and_inherits_the_rest()
    {
        var config = RunnerConfig.Parse("""
            defaults:
              models:
                implement: { default: m-d, complex: m-c }
                analyze: m-a
            projects:
              shop:
                models:
                  implement: { complex: m-strong }
                  analyze: m-b
                components:
                  web: { repo: o/shop }
            """);

        var settings = config.Projects.Single().Settings;

        Assert.Equal("m-d", settings.ModelSettingFor("implement")!.Default);
        Assert.Equal("m-strong", settings.ModelSettingFor("implement")!.Complex);
        Assert.Equal("m-b", settings.ModelSettingFor("analyze")!.Default);
    }

    [Fact]
    public void An_unknown_key_in_the_object_form_stops_the_start()
    {
        var exception = Fails("defaults:\n  models:\n    implement: { default: a, hard: b }\n" + Projects);

        Assert.Contains("hard", exception.Message);
    }

    [Fact]
    public void Fix_rounds_exists_for_implement_and_fix_only()
    {
        RunnerConfig.Parse("defaults:\n  models:\n    fix: { fix_rounds: m }\n" + Projects);

        var exception = Fails("defaults:\n  models:\n    review: { fix_rounds: m }\n" + Projects);

        Assert.Contains("fix_rounds", exception.Message);
    }

    [Fact]
    public void Allowed_models_check_models_and_agent_models_at_the_start()
    {
        var ok = RunnerConfig.Parse(
            "allowed_models: [m-a, m-b]\nagent_models: { code-scout: m-a }\n" +
            "defaults:\n  models: { review: m-b }\n" + Projects);

        Assert.Equal(["m-a", "m-b"], ok.AllowedModels);
        Assert.Equal("m-a", ok.AgentModels["code-scout"]);

        var badModel = Fails(
            "allowed_models: [m-a]\ndefaults:\n  models:\n    review: { default: m-a, complex: m-x }\n" + Projects);
        var badAgent = Fails("allowed_models: [m-a]\nagent_models: { code-scout: m-x }\n" + Projects);

        Assert.Contains("m-x", badModel.Message);
        Assert.Contains("allowed_models", badAgent.Message);
    }

    [Fact]
    public void An_empty_allow_list_checks_nothing()
    {
        var config = RunnerConfig.Parse(
            "agent_models: { code-scout: anything }\ndefaults:\n  models: { review: whatever }\n" + Projects);

        Assert.Empty(config.AllowedModels);
    }
}
