using System.Text.Json;
using System.Text.Json.Nodes;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Configuration;

/// <summary>
/// repos.yml as the Settings screen sees it: read as JSON, written back as YAML. A saved file must read back to the
/// same config, and a secret never leaves as text.
/// </summary>
public class SettingsJsonTests
{
    private const string LegacyShape = """
        git_bucket:
          base_url: http://gitbucket
          web_password: hunter2
        repos:
          - owner: o
            name: r
            workflows: [analyze, implement]
            models: { analyze: big-model }
            commands:
              build: npm run build
              test: ""
            review: { scope: scoped, skip_paths: ["**/*.md"] }
        """;

    private static string RealFile()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "repos.yml");

        return File.ReadAllText(path);
    }

    /// <summary>The resolved config as JSON, so two configs compare by value.</summary>
    private static string Resolved(string yaml)
    {
        var config = RunnerConfig.Parse(yaml);

        return JsonSerializer.Serialize(config);
    }

    private static string RoundTrip(string yaml)
    {
        var file = SettingsJson.Read(yaml);

        return SettingsJson.Write(file, yaml);
    }

    [Fact]
    public void The_real_repos_yml_reads_back_to_the_same_config_after_a_save()
    {
        var original = RealFile();

        var saved = RoundTrip(original);

        Assert.Equal(Resolved(original), Resolved(saved));
    }

    [Fact]
    public void A_second_save_writes_the_same_text()
    {
        var once = RoundTrip(RealFile());

        var twice = RoundTrip(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void The_old_git_bucket_block_and_repos_list_survive_a_save()
    {
        var saved = RoundTrip(LegacyShape);

        Assert.Equal(Resolved(LegacyShape), Resolved(saved));
        Assert.Contains("git_bucket:", saved);
        Assert.Contains("hunter2", saved);
        Assert.DoesNotContain("code_host:", saved);
    }

    [Fact]
    public void Keys_follow_the_order_of_todays_file()
    {
        var saved = RoundTrip(RealFile());
        var topKeys = saved
            .Split('\n')
            .Where(line => line.Length > 0 && char.IsLetter(line[0]))
            .Select(line => line[..line.IndexOf(':')])
            .ToList();

        Assert.Equal(
            ["code_host", "poll_interval_seconds", "runner_name", "copilot", "paths", "log_retention_days",
                "attachments", "dashboard", "git", "defaults", "projects"],
            topKeys);
    }

    [Fact]
    public void The_web_password_is_sent_as_is_set_only()
    {
        var file = SettingsJson.Read(LegacyShape);

        var text = file.ToJsonString();

        Assert.DoesNotContain("hunter2", text);
        Assert.Equal("""{"isSet":true}""", file["git_bucket"]!["web_password"]!.ToJsonString());
    }

    [Fact]
    public void A_web_password_sent_as_null_or_as_is_set_keeps_its_value()
    {
        var asRead = SettingsJson.Read(LegacyShape);
        var asNull = SettingsJson.Read(LegacyShape);
        asNull["git_bucket"]!["web_password"] = null;

        Assert.Contains("hunter2", SettingsJson.Write(asRead, LegacyShape));
        Assert.Contains("hunter2", SettingsJson.Write(asNull, LegacyShape));
    }

    [Fact]
    public void A_web_password_sent_as_text_is_refused_the_Setup_page_saves_it()
    {
        var file = SettingsJson.Read(LegacyShape);
        file["git_bucket"]!["web_password"] = "new-secret";

        var error = Assert.Throws<InvalidOperationException>(() => SettingsJson.Write(file, LegacyShape));

        Assert.Contains("Setup page", error.Message);
        Assert.DoesNotContain("new-secret", error.Message);
    }

    [Fact]
    public void An_unknown_key_is_refused_with_the_resolver_message()
    {
        var file = SettingsJson.Read(RealFile());
        file["code_host"]!["token"] = "abc";

        var error = Assert.Throws<InvalidOperationException>(() => SettingsJson.Write(file, RealFile()));

        Assert.Contains("unknown key 'token'", error.Message);
    }

    [Fact]
    public void An_invalid_value_is_refused_with_the_resolver_message()
    {
        var file = SettingsJson.Read(RealFile());
        file["defaults"]!["review"]!["scope"] = "everything";

        var error = Assert.Throws<InvalidOperationException>(() => SettingsJson.Write(file, RealFile()));

        Assert.Contains("review.scope 'everything' is unknown", error.Message);
    }

    [Fact]
    public void A_changed_value_is_written()
    {
        var file = SettingsJson.Read(RealFile());
        file["defaults"]!["limits"]!["max_diff_lines"] = 1234;

        var saved = SettingsJson.Write(file, RealFile());

        var config = RunnerConfig.Parse(saved);
        Assert.All(config.Projects, project => Assert.Equal(1234, project.Settings.Limits.MaxDiffLines));
    }

    [Fact]
    public void A_project_value_left_out_stays_inherited()
    {
        var yaml = """
            code_host: { kind: gitbucket, base_url: http://host }
            defaults: { never_edit: [a/] }
            projects:
              p:
                never_edit: []
                components: { c: { repo: o/r } }
            """;
        var file = SettingsJson.Read(yaml);

        var saved = SettingsJson.Write(file, yaml);

        Assert.Empty(RunnerConfig.Parse(saved).Projects[0].Settings.NeverEdit);

        var project = file["projects"]!["p"]!.AsObject();
        project.Remove("never_edit");
        var reset = SettingsJson.Write(file, yaml);

        Assert.Equal(["a/"], RunnerConfig.Parse(reset).Projects[0].Settings.NeverEdit);
    }

    [Fact]
    public void Effective_values_name_where_each_one_comes_from()
    {
        var yaml = """
            code_host: { kind: gitbucket, base_url: http://host }
            defaults: { limits: { max_diff_lines: 900 } }
            projects:
              p:
                limits: { max_reviews: 4 }
                components: { c: { repo: o/r } }
            """;

        var file = SettingsJson.Read(yaml);

        var effective = SettingsJson.Effective(file).Single();
        var byKey = effective.Values.ToDictionary(value => value.Key);

        Assert.Equal("p", effective.Project);
        var maxReviews = byKey["limits.max_reviews"];
        var maxDiffLines = byKey["limits.max_diff_lines"];
        var maxChangedFiles = byKey["limits.max_changed_files"];
        var implementModel = byKey["models.implement"];

        Assert.Equal("4", maxReviews.Effective.Value!.ToJsonString());
        Assert.Equal(("project", "Set in this project"), (maxReviews.Effective.Source, maxReviews.Effective.SourceLabel));
        Assert.Equal("3", maxReviews.Inherited.Value!.ToJsonString());
        Assert.Equal("default", maxReviews.Inherited.Source);
        Assert.Equal("900", maxDiffLines.Effective.Value!.ToJsonString());
        Assert.Equal(("defaults", "Inherited from defaults"), (maxDiffLines.Effective.Source, maxDiffLines.Effective.SourceLabel));
        Assert.Same(maxDiffLines.Effective, maxDiffLines.Inherited);
        Assert.Equal("15", maxChangedFiles.Effective.Value!.ToJsonString());
        Assert.Equal(("default", "Built-in default"), (maxChangedFiles.Effective.Source, maxChangedFiles.Effective.SourceLabel));
        Assert.Null(implementModel.Effective.Value);
    }

    [Fact]
    public void Comments_are_found()
    {
        Assert.True(SettingsJson.HasComments(RealFile()));
        Assert.False(SettingsJson.HasComments("poll_interval_seconds: 5  \nrunner_name: 'a # b'\n"));
    }
}
