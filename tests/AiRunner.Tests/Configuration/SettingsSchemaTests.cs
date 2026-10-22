using System.Reflection;
using YamlDotNet.Serialization.NamingConventions;

namespace AiRunner.Tests.Configuration;

/// <summary>
/// The schema the Settings screen is built from must cover every key the resolver accepts, and nothing else: a key
/// added to the file types without a schema entry fails here.
/// </summary>
public class SettingsSchemaTests
{
    /// <summary>Every key path the strict reader accepts, from the raw file types (internal, so found by name).</summary>
    private static List<string> AcceptedPaths()
    {
        var fileType = typeof(RunnerConfig).Assembly.GetType("AiRunner.Configuration.RunnerFile", throwOnError: true)!;
        var paths = new List<string>();
        Walk(fileType, "", paths);

        return paths;
    }

    private static void Walk(Type type, string prefix, List<string> paths)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite);

        foreach (var property in properties)
        {
            var key = UnderscoredNamingConvention.Instance.Apply(property.Name);
            var path = prefix + key;
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (propertyType == typeof(Dictionary<string, string>))
            {
                // models: the resolver accepts exactly the workflows that run Copilot.
                paths.AddRange(Workflows.WithModel.Select(workflow => $"{path}.{workflow}"));
            }
            else if (IsGeneric(propertyType, typeof(Dictionary<,>)))
            {
                Walk(propertyType.GetGenericArguments()[1], path + ".*.", paths);
            }
            else if (IsGeneric(propertyType, typeof(List<>)) && propertyType != typeof(List<string>))
            {
                Walk(propertyType.GetGenericArguments()[0], path + "[].", paths);
            }
            else if (propertyType.IsClass && propertyType != typeof(string) && propertyType != typeof(List<string>))
            {
                Walk(propertyType, path + ".", paths);
            }
            else
            {
                paths.Add(path);
            }
        }
    }

    private static bool IsGeneric(Type type, Type definition) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == definition;

    [Fact]
    public void Every_key_the_resolver_accepts_has_a_schema_entry()
    {
        var schemaPaths = SettingsSchema.All
            .Select(entry => entry.Path)
            .ToHashSet();

        var missing = AcceptedPaths().Where(path => !schemaPaths.Contains(path)).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_schema_entry_is_a_key_the_resolver_accepts()
    {
        var accepted = AcceptedPaths().ToHashSet();

        var unknown = SettingsSchema.All
            .Select(entry => entry.Path)
            .Where(path => !accepted.Contains(path))
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void Each_path_has_one_entry_with_help()
    {
        var duplicates = SettingsSchema.All
            .GroupBy(entry => entry.Path)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        Assert.Empty(duplicates);
        Assert.All(SettingsSchema.All, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Help), entry.Path));
    }

    [Fact]
    public void The_spec_keys_and_the_runner_name_need_a_restart()
    {
        var restartNeeded = SettingsSchema.All
            .Where(entry => entry.IsRestartNeeded && entry.Scope == SettingScopes.Global)
            .Select(entry => entry.Path)
            .Order()
            .ToList();

        Assert.Equal(
            ["code_host.base_url", "code_host.kind", "code_host.web_password", "dashboard.enabled", "dashboard.port",
                "paths.logs", "paths.state", "paths.work", "runner_name"],
            restartNeeded);
        Assert.Equal(restartNeeded, RunnerConfigSource.RestartNeededKeys.Order());
    }

    [Fact]
    public void Only_the_web_passwords_are_secret()
    {
        var secrets = SettingsSchema.All
            .Where(entry => entry.IsSecret)
            .Select(entry => entry.Path)
            .Order()
            .ToList();

        Assert.Equal(["code_host.web_password", "git_bucket.web_password"], secrets);
    }

    [Fact]
    public void Defaults_and_projects_share_the_same_setting_keys()
    {
        var defaultKeys = SettingsSchema.All
            .Where(entry => entry.Scope == SettingScopes.Defaults)
            .Select(entry => entry.Key);
        var projectKeys = SettingsSchema.All
            .Where(entry => entry.Scope == SettingScopes.Project)
            .Select(entry => entry.Key)
            .ToHashSet();

        Assert.All(defaultKeys, key => Assert.Contains(key, projectKeys));
        Assert.Equal(15, SettingsSchema.ProjectSettings.Single(entry => entry.Key == "limits.max_changed_files").Default!.GetValue<int>());
    }
}
