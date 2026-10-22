using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace AiRunner.Configuration;

/// <summary>
/// Writes a <see cref="RunnerFile"/> as repos.yml: snake_case, in the order of the file types (today's key order), and
/// only what is written (a null stays out, so "not written" stays different from "written as the default"). Lists of
/// text are written on one line (<c>[a, b]</c>), as people write them by hand. Comments are not kept; their text lives
/// on as help in <see cref="SettingsSchema"/>.
/// </summary>
internal static class RunnerFileWriter
{
    private static readonly ISerializer Writer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithTypeConverter(new ModelSetting.YamlConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .DisableAliases()
        .WithEventEmitter(next => new TextListsOnOneLine(next))
        .Build();

    public static string Write(RunnerFile file) => Writer.Serialize(file);

    private sealed class TextListsOnOneLine(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(SequenceStartEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(List<string>))
            {
                eventInfo.Style = SequenceStyle.Flow;
            }

            base.Emit(eventInfo, emitter);
        }
    }
}
