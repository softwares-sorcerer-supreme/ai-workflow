using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace AiRunner.Configuration;

/// <summary>
/// The model choice of one workflow: <c>models.implement: claude-sonnet-5.5</c> (only <see cref="Default"/>) or the
/// object form with <c>default</c>, <c>simple</c>, <c>complex</c> and <c>fix_rounds</c>. A key that is not written is
/// null, so a project can override one key and inherit the rest. Which one applies is <c>ModelResolver</c>'s call.
/// </summary>
[JsonConverter(typeof(ModelSettingJsonConverter))]
public sealed record ModelSetting
{
    public const string DefaultKey = "default";
    public const string SimpleKey = "simple";
    public const string ComplexKey = "complex";
    public const string FixRoundsKey = "fix_rounds";

    public static readonly string[] Keys = [DefaultKey, SimpleKey, ComplexKey, FixRoundsKey];

    public string? Default { get; init; }

    /// <summary>
    /// Read from the plain string form. That form replaces whatever an earlier layer wrote for the workflow, as it always
    /// did, instead of merging key by key like the object form.
    /// </summary>
    [JsonIgnore]
    public bool IsPlainString { get; init; }

    public string? Simple { get; init; }
    public string? Complex { get; init; }

    /// <summary>For implement and fix only: the model of the Copilot runs after round 1.</summary>
    public string? FixRounds { get; init; }

    /// <summary>An empty list allows every model (no check, as before).</summary>
    public static bool IsAllowed(IReadOnlyCollection<string> allowedModels, string model) =>
        allowedModels.Count == 0 || allowedModels.Contains(model, StringComparer.Ordinal);

    /// <summary>Every model name this setting holds.</summary>
    public IEnumerable<string> AllModels() =>
        new[] { Default, Simple, Complex, FixRounds }
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model!);

    /// <summary>Key by key: what <paramref name="layer"/> writes wins, the rest stays (defaults, then a project).</summary>
    public ModelSetting MergedWith(ModelSetting layer)
    {
        if (layer.IsPlainString)
        {
            return layer with { IsPlainString = false };
        }

        return new ModelSetting
        {
            Default = layer.Default ?? Default,
            Simple = layer.Simple ?? Simple,
            Complex = layer.Complex ?? Complex,
            FixRounds = layer.FixRounds ?? FixRounds,
        };
    }

    /// <summary>The value of a key by its file name, or null.</summary>
    public string? ValueOf(string key) => key switch
    {
        DefaultKey => Default,
        SimpleKey => Simple,
        ComplexKey => Complex,
        FixRoundsKey => FixRounds,
        _ => null,
    };

    /// <summary>The setting with one key set; an unknown key is a mistake of the caller.</summary>
    public ModelSetting With(string key, string? value) => key switch
    {
        DefaultKey => this with { Default = value },
        SimpleKey => this with { Simple = value },
        ComplexKey => this with { Complex = value },
        FixRoundsKey => this with { FixRounds = value },
        _ => throw new InvalidOperationException($"Unknown model key '{key}'. Use: {string.Join(", ", Keys)}"),
    };

    /// <summary>Reads the scalar or mapping form; an unknown key stops the start.</summary>
    internal sealed class YamlConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ModelSetting);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                return new ModelSetting { Default = scalar.Value, IsPlainString = true };
            }

            parser.Consume<MappingStart>();
            var setting = new ModelSetting();

            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>();
                var value = parser.Consume<Scalar>();

                if (!Keys.Contains(key.Value))
                {
                    throw new YamlException(
                        key.Start, key.End,
                        $"models: unknown key '{key.Value}'. Use: {string.Join(", ", Keys)}");
                }

                setting = setting.With(key.Value, value.Value);
            }

            return setting;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var setting = (ModelSetting)value!;

            // Only the plain string goes back as a string: the object form must stay an object (it merges).
            if (setting.IsPlainString)
            {
                emitter.Emit(new Scalar(setting.Default ?? ""));
                return;
            }

            emitter.Emit(new MappingStart(null, null, isImplicit: true, MappingStyle.Block));

            foreach (var key in Keys)
            {
                var text = setting.ValueOf(key);
                if (text is null)
                {
                    continue;
                }

                emitter.Emit(new Scalar(key));
                emitter.Emit(new Scalar(text));
            }

            emitter.Emit(new MappingEnd());
        }
    }
}

/// <summary>
/// The JSON of the Settings screen: always the object form (one shape for the form to edit), and a plain string is
/// read as <c>default</c>. Null keys stay out.
/// </summary>
internal sealed class ModelSettingJsonConverter : JsonConverter<ModelSetting>
{
    public override ModelSetting Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new ModelSetting { Default = reader.GetString(), IsPlainString = true };
        }

        var setting = new ModelSetting();

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("models.<workflow> is a model name or an object.");
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString() ?? "";
            reader.Read();
            var value = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

            if (!ModelSetting.Keys.Contains(key))
            {
                throw new JsonException($"models: unknown key '{key}'. Use: {string.Join(", ", ModelSetting.Keys)}");
            }

            setting = setting.With(key, value);
        }

        return setting;
    }

    public override void Write(Utf8JsonWriter writer, ModelSetting value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var key in ModelSetting.Keys)
        {
            var text = value.ValueOf(key);
            if (text is not null)
            {
                writer.WriteString(key, text);
            }
        }

        writer.WriteEndObject();
    }
}
