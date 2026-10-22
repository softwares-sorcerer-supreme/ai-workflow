using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Tokens;

namespace AiRunner.Configuration;

/// <summary>A value of a project setting and where it comes from, with the text the screen shows for that.</summary>
/// <param name="Value">The value as written in repos.yml (JSON); null = not set anywhere (no built-in value).</param>
/// <param name="Source">
/// <c>project</c>, <c>defaults</c> or <c>default</c> (built in); <c>project</c> also when the project's plain model
/// name replaces the whole <c>models.&lt;workflow&gt;</c> entry, so nothing of it is inherited.
/// </param>
public sealed record SourcedValue(JsonNode? Value, string Source, string SourceLabel);

/// <summary>
/// One setting of a project after the merge (<see cref="Effective"/>), and what would apply without the project's own
/// value (<see cref="Inherited"/>: from <c>defaults:</c> or built in), so a reset shows its result before a save.
/// </summary>
public sealed record EffectiveValue(string Key, SourcedValue Effective, SourcedValue Inherited);

/// <summary>The merged settings of one project, key by key.</summary>
public sealed record ProjectEffective(string Project, IReadOnlyList<EffectiveValue> Values);

/// <summary>What the Settings screen reads: the file as JSON and the merged settings of every project.</summary>
public sealed record SettingsFileView(JsonObject File, IReadOnlyList<ProjectEffective> Effective);

/// <summary>
/// repos.yml as the Settings screen edits it: the raw file as snake_case JSON (only what is written, secrets as
/// <c>{ "isSet": true|false }</c>), and back. The way back goes through the same strict reader and resolver as the
/// start (JSON is YAML), and the written text must read back to the same config, so a save can never write a file
/// the runner would read differently. The raw file types stay inside Configuration/.
/// </summary>
public static class SettingsJson
{
    public const string FromProject = "project";
    public const string FromDefaults = "defaults";
    public const string BuiltIn = "default";

    private const string IsSetKey = "isSet";

    private static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The built-in project settings; only read (a value handed out is cloned).</summary>
    private static readonly JsonObject BuiltInSettings =
        JsonSerializer.SerializeToNode(new ProjectSettings(), FileJson)!.AsObject();

    public const string BuiltInLabel = "Built-in default";
    public const string NotSetLabel = "Not set.";

    private static readonly Dictionary<string, string> SourceLabels = new()
    {
        [FromProject] = "Set in this project",
        [FromDefaults] = "Inherited from defaults",
        [BuiltIn] = BuiltInLabel,
    };

    private const string ReplacedByPlainModel = "Not inherited (this project names one model for the workflow)";

    /// <summary>
    /// The file as JSON, every secret masked. Throws <see cref="InvalidOperationException"/> on a bad file.
    /// </summary>
    public static JsonObject Read(string yaml) => ReadView(yaml).File;

    /// <summary>
    /// The file as JSON (every secret masked) and the merged settings of every project (<see cref="Effective"/>), from
    /// one read of the text. Throws <see cref="InvalidOperationException"/> on a bad file.
    /// </summary>
    public static SettingsFileView ReadView(string yaml)
    {
        var runnerFile = ConfigResolver.Deserialize(yaml);
        var json = ToJson(runnerFile);

        foreach (var secret in SettingsSchema.All.Where(entry => entry.IsSecret))
        {
            MaskSecret(json, secret.Path);
        }

        var effective = Effective(runnerFile, json);

        return new SettingsFileView(json, effective);
    }

    /// <summary>
    /// The new text of repos.yml for an edited <paramref name="file"/>. A secret sent as null or as its
    /// <c>{ isSet }</c> mask keeps its value in <paramref name="currentYaml"/>, a secret sent as text is refused; any
    /// other null means "not written".
    /// Throws <see cref="InvalidOperationException"/> with the resolver's message when the result is invalid.
    /// </summary>
    public static string Write(JsonObject file, string currentYaml)
    {
        var incoming = file.DeepClone().AsObject();
        var currentFile = ConfigResolver.Deserialize(currentYaml);
        var current = ToJson(currentFile);

        foreach (var secret in SettingsSchema.All.Where(entry => entry.IsSecret))
        {
            RestoreSecret(incoming, current, secret.Path);
        }

        RemoveNulls(incoming);

        var text = incoming.ToJsonString(Indented);
        var runnerFile = ConfigResolver.Deserialize(text);
        KeepPlainModels(runnerFile, currentFile);
        var resolved = ConfigResolver.Resolve(runnerFile);
        RunnerConfig.RequireBaseUrl(resolved.Config, "the new settings");

        var yaml = RunnerFileWriter.Write(runnerFile);
        var readBack = RunnerConfig.ParseFile(yaml);
        if (!IsSameConfig(resolved, readBack))
        {
            throw new InvalidOperationException(
                "The new repos.yml would read back differently from what was sent; nothing was saved.");
        }

        return yaml;
    }

    /// <summary>
    /// Per project of <c>projects:</c> in repos.yml, every setting of <see cref="SettingsSchema.ProjectSettings"/>: the
    /// project's own value, else the one of <c>defaults:</c>, else the built-in one (key by key, like the resolver
    /// merges). A plain model name of the project replaces the whole entry, so its other keys inherit nothing.
    /// </summary>
    public static IReadOnlyList<ProjectEffective> Effective(string yaml) => ReadView(yaml).Effective;

    /// <summary>
    /// The plain names come from <paramref name="runnerFile"/>: the JSON of <paramref name="file"/> always holds the
    /// object form of a model setting.
    /// </summary>
    private static IReadOnlyList<ProjectEffective> Effective(RunnerFile runnerFile, JsonObject file)
    {
        var defaults = file["defaults"] as JsonObject ?? [];
        var projects = file["projects"] as JsonObject ?? [];
        var result = new List<ProjectEffective>();

        foreach (var (name, node) in projects)
        {
            var plainModels = PlainModelsOf(runnerFile.Projects[name].Models);
            var layers = new SettingLayers(node as JsonObject ?? [], defaults, BuiltInSettings, plainModels);
            var values = SettingsSchema.ProjectSettings
                .Select(entry => Effective(entry.Key, layers))
                .ToList();
            result.Add(new ProjectEffective(name, values));
        }

        return result;
    }

    /// <summary>Whether the text holds YAML comments, which a save drops (the screen warns once).</summary>
    public static bool HasComments(string yaml)
    {
        try
        {
            var scanner = new Scanner(new StringReader(yaml), skipComments: false);
            while (scanner.MoveNext())
            {
                if (scanner.Current is Comment)
                {
                    return true;
                }
            }
        }
        catch (YamlException)
        {
            // A broken file is reported by the reload; here it only means "no comments found".
        }

        return false;
    }

    private static SourcedValue Sourced(JsonNode? value, string source) => new(value, source, SourceLabels[source]);

    private static JsonObject ToJson(RunnerFile file) => JsonSerializer.SerializeToNode(file, FileJson)!.AsObject();

    private static EffectiveValue Effective(string key, SettingLayers layers)
    {
        var (project, defaults, builtIn, plainModels) = layers;
        SourcedValue inherited;
        if (IsReplacedByPlainModel(key, plainModels))
        {
            inherited = new SourcedValue(null, FromProject, ReplacedByPlainModel);
        }
        else if (Find(defaults, key) is { } shared)
        {
            inherited = Sourced(shared.DeepClone(), FromDefaults);
        }
        else
        {
            var builtInValue = Find(builtIn, key)?.DeepClone();
            inherited = Sourced(builtInValue, BuiltIn);
        }

        var effective = inherited;
        if (Find(project, key) is { } own)
        {
            effective = Sourced(own.DeepClone(), FromProject);
        }

        return new EffectiveValue(key, effective, inherited);
    }

    /// <summary>The node at a dotted path (no wildcards), or null when any part is missing or null.</summary>
    private static JsonNode? Find(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            if (node is not JsonObject parent || !parent.TryGetPropertyValue(part, out node))
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>The object holding the last part of a dotted path, and that part; null when it is missing.</summary>
    private static (JsonObject Parent, string Key)? ParentOf(JsonObject root, string path)
    {
        var lastDot = path.LastIndexOf('.');
        var key = path[(lastDot + 1)..];
        if (lastDot < 0)
        {
            return (root, key);
        }

        var parentPath = path[..lastDot];
        if (Find(root, parentPath) is JsonObject parent)
        {
            return (parent, key);
        }

        return null;
    }

    private static void MaskSecret(JsonObject json, string path)
    {
        if (ParentOf(json, path) is not { } location || !location.Parent.ContainsKey(location.Key))
        {
            return;
        }

        var (parent, key) = location;

        var value = parent[key]?.GetValue<string>();
        parent[key] = new JsonObject { [IsSetKey] = !string.IsNullOrEmpty(value) };
    }

    private static void RestoreSecret(JsonObject incoming, JsonObject current, string path)
    {
        if (ParentOf(incoming, path) is not { } location ||
            !location.Parent.TryGetPropertyValue(location.Key, out var sent))
        {
            return;
        }

        var (parent, key) = location;

        // Secrets are not settings (docs/specs/dashboard-control.md section 8): a new one is saved on the Setup page,
        // never written into repos.yml from here. Null or the mask means "keep what the file has".
        if (sent is JsonValue)
        {
            throw new InvalidOperationException(
                $"{path} cannot be changed on the Settings screen. Save passwords on the Setup page.");
        }

        var kept = Find(current, path);
        if (kept is null)
        {
            parent.Remove(key);
        }
        else
        {
            parent[key] = kept.DeepClone();
        }
    }

    /// <summary>Drops every property that is null: not written, so the merge falls back to the layer below.</summary>
    private static void RemoveNulls(JsonObject json)
    {
        var nullKeys = json
            .Where(property => property.Value is null)
            .Select(property => property.Key)
            .ToList();

        foreach (var key in nullKeys)
        {
            json.Remove(key);
        }

        foreach (var (_, value) in json)
        {
            if (value is JsonObject child)
            {
                RemoveNulls(child);
            }
            else if (value is JsonArray list)
            {
                foreach (var entry in list.OfType<JsonObject>())
                {
                    RemoveNulls(entry);
                }
            }
        }
    }

    /// <summary>
    /// The workflows whose <c>models</c> entry is a plain model name: in the merge it replaces the entry of
    /// <c>defaults:</c> instead of merging key by key.
    /// </summary>
    private static HashSet<string> PlainModelsOf(Dictionary<string, ModelSetting>? models)
    {
        var plain = (models ?? [])
            .Where(entry => entry.Value.IsPlainString)
            .Select(entry => entry.Key);

        return [.. plain];
    }

    /// <summary>
    /// <c>models.&lt;workflow&gt;.&lt;key&gt;</c> other than <c>default</c> of a workflow in the set.
    /// </summary>
    private static bool IsReplacedByPlainModel(string key, HashSet<string> plainModels)
    {
        if (plainModels.Count == 0)
        {
            return false;
        }

        var parts = key.Split('.');

        return parts.Length == 3 &&
            parts[0] == "models" &&
            parts[2] != ModelSetting.DefaultKey &&
            plainModels.Contains(parts[1]);
    }

    /// <summary>
    /// The screen edits every model setting in the object form, so a plain <c>implement: x</c> comes back as
    /// <c>{ default: x }</c>. Written like that it would merge with <c>defaults:</c> key by key instead of replacing
    /// it, and a save would change which model runs. An entry that still holds only <c>default</c> and was a plain
    /// name in the file stays one.
    /// </summary>
    private static void KeepPlainModels(RunnerFile edited, RunnerFile current)
    {
        KeepPlain(edited.Defaults.Models, current.Defaults.Models);

        foreach (var (name, project) in edited.Projects)
        {
            if (current.Projects.TryGetValue(name, out var before))
            {
                KeepPlain(project.Models, before.Models);
            }
        }

        var currentRepos = current.Repos ?? [];
        foreach (var repo in edited.Repos ?? [])
        {
            var before = currentRepos.FirstOrDefault(entry => entry.Owner == repo.Owner && entry.Name == repo.Name);
            if (before is not null)
            {
                KeepPlain(repo.Models, before.Models);
            }
        }
    }

    private static void KeepPlain(Dictionary<string, ModelSetting>? edited, Dictionary<string, ModelSetting>? current)
    {
        if (edited is null || current is null)
        {
            return;
        }

        foreach (var (workflow, setting) in edited.ToList())
        {
            // Record equality covers every key, also one added later.
            var isOnlyDefault = setting.Default is not null &&
                setting == new ModelSetting { Default = setting.Default };
            var wasPlain = current.TryGetValue(workflow, out var before) && before.IsPlainString;

            if (isOnlyDefault && wasPlain)
            {
                edited[workflow] = setting with { IsPlainString = true };
            }
        }
    }

    /// <summary>Where a project's setting may come from, first match wins.</summary>
    private sealed record SettingLayers(
        JsonObject Project,
        JsonObject Defaults,
        JsonObject BuiltIn,
        HashSet<string> PlainModels);

    private static bool IsSameConfig(ConfigFile left, ConfigFile right)
    {
        var leftJson = JsonSerializer.Serialize(left.Config);
        var rightJson = JsonSerializer.Serialize(right.Config);

        return leftJson == rightJson && left.WebPassword == right.WebPassword;
    }
}
