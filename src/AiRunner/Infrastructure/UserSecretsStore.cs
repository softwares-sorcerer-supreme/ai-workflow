using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace AiRunner.Infrastructure;

/// <summary>
/// The .NET user-secrets file of this project (the one <c>dotnet user-secrets set</c> writes), read and written by the
/// runner itself so the dashboard's Setup page can save the token. Plain JSON in the user's profile: the same
/// protection as before, nothing more (ADR 0004). A write keeps every other key and replaces the file whole, so a
/// crash never leaves half a file. Like the <c>dotnet user-secrets</c> tool, a write stores every key flat
/// (<c>"GitBucket:Token"</c>), which the configuration reader treats the same as a nested object.
/// </summary>
/// <param name="path">The secrets file; tests pass a temp file, never the real one.</param>
public sealed class UserSecretsStore(string path)
{
    public const string TokenKey = "GitBucket:Token";
    public const string GitPasswordKey = "GitBucket:GitPassword";
    public const string WebPasswordKey = "GitBucket:WebPassword";

    private readonly Lock _lock = new();

    public string FilePath { get; } = path;

    /// <summary>The secrets file of the assembly's <c>UserSecretsId</c> (the csproj property).</summary>
    public static UserSecretsStore ForAssembly(Assembly assembly)
    {
        var attribute = assembly.GetCustomAttribute<UserSecretsIdAttribute>()
            ?? throw new InvalidOperationException($"{assembly.GetName().Name} has no UserSecretsId.");
        var secretsPath = PathHelper.GetSecretsPathFromSecretsId(attribute.UserSecretsId);

        return new UserSecretsStore(secretsPath);
    }

    /// <summary>Every key, nested objects flattened to <c>a:b</c>; empty when the file does not exist.</summary>
    public IReadOnlyDictionary<string, string> Read()
    {
        lock (_lock)
        {
            return ReadFile();
        }
    }

    public string? Get(string key) => Read().GetValueOrDefault(key);

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            var values = ReadFile();
            values[key] = value;
            WriteFile(values);
        }
    }

    /// <summary>Removes the key; a file without it stays as it is.</summary>
    public void Remove(string key)
    {
        lock (_lock)
        {
            var values = ReadFile();
            if (!values.Remove(key))
            {
                return;
            }

            WriteFile(values);
        }
    }

    private Dictionary<string, string> ReadFile()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(FilePath))
        {
            return values;
        }

        var text = File.ReadAllText(FilePath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return values;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException)
        {
            // The parser's message quotes the text near the error, which may be a secret: name the file only.
            throw new InvalidDataException($"The user-secrets file is not valid JSON: {FilePath}");
        }

        if (root is not JsonObject rootObject)
        {
            throw new InvalidDataException($"The user-secrets file does not hold a JSON object: {FilePath}");
        }

        Flatten(rootObject, "", values);

        return values;
    }

    private static void Flatten(JsonNode? node, string prefix, Dictionary<string, string> values)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var (name, child) in jsonObject)
                {
                    var key = prefix.Length == 0 ? name : $"{prefix}:{name}";
                    Flatten(child, key, values);
                }

                break;
            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    Flatten(jsonArray[index], $"{prefix}:{index}", values);
                }

                break;
            case JsonValue jsonValue:
                var text = jsonValue.GetValueKind() == JsonValueKind.String
                    ? jsonValue.GetValue<string>()
                    : jsonValue.ToJsonString();
                values[prefix] = text;
                break;
            default:
                // A JSON null: the configuration reader keeps it as an empty value.
                values[prefix] = "";
                break;
        }
    }

    /// <summary>A temp file beside the target, then one move over it: readers see the old file or the new.</summary>
    private void WriteFile(Dictionary<string, string> values)
    {
        var folder = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(folder);

        var json = new JsonObject();
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            json[key] = value;
        }

        var text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tempFile = Path.Combine(folder, $"{Path.GetFileName(FilePath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempFile, text);
            File.Move(tempFile, FilePath, overwrite: true);
        }
        finally
        {
            FileSystem.DeleteFile(tempFile);
        }
    }
}
