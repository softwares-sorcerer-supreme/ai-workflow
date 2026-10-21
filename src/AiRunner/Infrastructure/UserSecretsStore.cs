using System.Reflection;
using System.Text;
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

        // The configuration reader itself, so the keys are exactly the ones the runner's configuration would see.
        IConfigurationRoot configuration;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            configuration = new ConfigurationBuilder()
                .AddJsonStream(stream)
                .Build();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            // The parser's message quotes the text near the error, which may be a secret: name the file only.
            throw new InvalidDataException($"The user-secrets file is not a valid JSON object: {FilePath}");
        }

        foreach (var (key, value) in configuration.AsEnumerable())
        {
            if (value is not null)
            {
                values[key] = value;
            }
        }

        return values;
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
