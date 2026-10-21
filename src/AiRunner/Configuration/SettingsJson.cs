using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Tokens;

namespace AiRunner.Configuration;

/// <summary>One setting of a project after the merge, and where its value comes from.</summary>
/// <param name="Value">The value as written in repos.yml (JSON); null = not set anywhere (no built-in value).</param>
/// <param name="Source"><c>project</c>, <c>defaults</c> or <c>default</c> (built in).</param>
public sealed record EffectiveValue(string Key, JsonNode? Value, string Source);

/// <summary>The merged settings of one project, key by key.</summary>
public sealed record ProjectEffective(string Project, IReadOnlyList<EffectiveValue> Values);

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

    /// <summary>The file as JSON, every secret masked. Throws <see cref="InvalidOperationException"/> on a bad file.</summary>
    public static JsonObject Read(string yaml)
    {
        var file = ConfigResolver.Deserialize(yaml);
        var json = ToJson(file);

        foreach (var secret in SettingsSchema.All.Where(entry => entry.IsSecret))
        {
            MaskSecret(json, secret.Path);
        }

        return json;
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
    /// Per project of <c>projects:</c>, every setting of <see cref="SettingsSchema.ProjectSettings"/>: the project's
    /// own value, else the one of <c>defaults:</c>, else the built-in one (key by key, like the resolver merges).
    /// </summary>
    public static IReadOnlyList<ProjectEffective> Effective(string yaml)
    {
        var file = ConfigResolver.Deserialize(yaml);
        var json = ToJson(file);
        var defaults = json["defaults"] as JsonObject ?? [];
        var builtIn = JsonSerializer.SerializeToNode(new ProjectSettings(), FileJson)!.AsObject();
        var projects = json["projects"] as JsonObject ?? [];
        var result = new List<ProjectEffective>();

        foreach (var (name, node) in projects)
        {
            var layers = new SettingLayers(node as JsonObject ?? [], defaults, builtIn);
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

    private static JsonObject ToJson(RunnerFile file) => JsonSerializer.SerializeToNode(file, FileJson)!.AsObject();

    private static EffectiveValue Effective(string key, SettingLayers layers)
    {
        var (project, defaults, builtIn) = layers;
        if (Find(project, key) is { } own)
        {
            return new EffectiveValue(key, own.DeepClone(), FromProject);
        }

        if (Find(defaults, key) is { } shared)
        {
            return new EffectiveValue(key, shared.DeepClone(), FromDefaults);
        }

        var builtInValue = Find(builtIn, key)?.DeepClone();

        return new EffectiveValue(key, builtInValue, BuiltIn);
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

    /// <summary>Where a project's setting may come from, first match wins.</summary>
    private sealed record SettingLayers(JsonObject Project, JsonObject Defaults, JsonObject BuiltIn);

    private static bool IsSameConfig(ConfigFile left, ConfigFile right)
    {
        var leftJson = JsonSerializer.Serialize(left.Config);
        var rightJson = JsonSerializer.Serialize(right.Config);

        return leftJson == rightJson && left.WebPassword == right.WebPassword;
    }
}
