using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>
/// The model a person picked for one issue (dashboard action <c>set-model</c>): a bot comment holding
/// <c>&lt;!-- AI_SETTINGS models.implement=claude-opus-5.5 --&gt;</c>. Only bot comments count, so nobody can fake it
/// by typing the tag; the newest tag of a key wins and an empty value removes the override. A sub-issue reads its own
/// tags, then its parent's.
/// </summary>
public static partial class ModelOverride
{
    private const string KeyPrefix = "models.";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@-]*$")]
    private static partial Regex NamePattern();

    public static string KeyOf(string workflow) => KeyPrefix + workflow;

    /// <summary>Whether a model name is safe to write into a tag (no space, no <c>--&gt;</c>).</summary>
    public static bool IsValidName(string model) => NamePattern().IsMatch(model) && !model.Contains("--", StringComparison.Ordinal);

    /// <summary>The comment text that sets (or, with an empty model, removes) the override.</summary>
    public static string CommentText(string workflow, string model, string visibleLine)
    {
        var tag = Markers.SettingTag(KeyOf(workflow), model);

        return $"{BotComments.Marker}\n{BotComments.VisibleTag} {visibleLine}\n{tag}";
    }

    /// <summary>
    /// The override of a workflow in these comments: null = no tag at all, "" = the newest tag removed the override,
    /// else the model.
    /// </summary>
    public static string? Find(string workflow, IEnumerable<HostComment> comments)
    {
        var key = KeyOf(workflow);

        var tags = comments
            .Where(BotComments.IsBot)
            .OrderBy(comment => comment.Id)
            .SelectMany(comment => Markers.ReadSettings(comment.Body))
            .Where(setting => setting.Key == key)
            .ToList();

        return tags.Count == 0 ? null : tags[^1].Value;
    }

    /// <summary>The model that applies: the issue's own tag, else its parent's; null when there is none.</summary>
    public static string? For(string workflow, IEnumerable<HostComment> own, IEnumerable<HostComment>? parent)
    {
        var ownValue = Find(workflow, own);

        if (ownValue is not null)
        {
            return ownValue.Length == 0 ? null : ownValue;
        }

        var parentValue = parent is null ? null : Find(workflow, parent);

        return string.IsNullOrEmpty(parentValue) ? null : parentValue;
    }
}
