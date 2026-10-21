using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AiRunner.Core;

/// <summary>
/// The checks a saved agent or skill file must pass (docs/specs/dashboard-control.md section 7). It returns every
/// reason, so the editor can show them all at once; an empty list means the file may be saved. Unknown frontmatter
/// keys are fine (Copilot reads keys this runner does not know). Other files under skills/ have no frontmatter.
/// </summary>
public static class AgentFileRules
{
    private const string FrontmatterFence = "---";

    public static IReadOnlyList<string> Check(
        string path,
        string text,
        IReadOnlyCollection<string>? allowedModels = null)
    {
        var kind = AgentPaths.KindOf(path);
        var isSkillDefinition = kind == AgentKinds.Skill
            && path.EndsWith("/" + AgentPaths.SkillFile, StringComparison.Ordinal);
        if (kind != AgentKinds.Agent && !isSkillDefinition)
        {
            return [];
        }

        var frontmatter = FrontmatterOf(text);
        if (frontmatter is null)
        {
            return ["The file must start with a frontmatter block between two --- lines."];
        }

        YamlMappingNode root;
        try
        {
            root = ParseMapping(frontmatter);
        }
        catch (YamlException exception)
        {
            return [$"The frontmatter is not valid YAML: {exception.Message}"];
        }
        catch (InvalidOperationException exception)
        {
            return [exception.Message];
        }

        var reasons = new List<string>();
        if (kind == AgentKinds.Agent)
        {
            var fileName = path[(path.LastIndexOf('/') + 1)..];
            var stem = fileName[..^AgentPaths.AgentSuffix.Length];
            CheckAgent(stem, root, reasons);
            CheckModel(root, allowedModels, reasons);
        }
        else
        {
            CheckSkill(root, reasons);
        }

        return reasons;
    }

    private static void CheckAgent(string stem, YamlMappingNode root, List<string> reasons)
    {
        var name = ScalarOf(root, "name");
        if (name != stem)
        {
            reasons.Add($"name must be \"{stem}\" (the file name without {AgentPaths.AgentSuffix}).");
        }

        CheckDescription(root, reasons);

        if (root.Children.TryGetValue(new YamlScalarNode("tools"), out var tools))
        {
            var isListOfStrings = tools is YamlSequenceNode list && list.Children.All(item => item is YamlScalarNode);
            if (!isListOfStrings)
            {
                reasons.Add("tools must be a list of strings.");
            }
        }

    }

    private static void CheckModel(YamlMappingNode root, IReadOnlyCollection<string>? allowedModels, List<string> reasons)
    {
        var model = ScalarOf(root, "model");
        var hasAllowList = allowedModels is { Count: > 0 };
        if (model is not null && hasAllowList && !allowedModels!.Contains(model))
        {
            reasons.Add($"model \"{model}\" is not in allowed_models.");
        }
    }

    private static void CheckSkill(YamlMappingNode root, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(ScalarOf(root, "name")))
        {
            reasons.Add("name is required.");
        }

        CheckDescription(root, reasons);
    }

    private static void CheckDescription(YamlMappingNode root, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(ScalarOf(root, "description")))
        {
            reasons.Add("description is required and must not be empty.");
        }
    }

    private static string? ScalarOf(YamlMappingNode root, string key) =>
        root.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static YamlMappingNode ParseMapping(string frontmatter)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(frontmatter));

        var isMapping = stream.Documents.Count == 1 && stream.Documents[0].RootNode is YamlMappingNode;
        if (!isMapping)
        {
            throw new InvalidOperationException("The frontmatter must be a list of key: value lines.");
        }

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    /// <summary>The text between the first two --- lines, or null when the file does not start with a block.</summary>
    private static string? FrontmatterOf(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 3 || lines[0].TrimEnd() != FrontmatterFence)
        {
            return null;
        }

        var end = Array.FindIndex(lines, 1, line => line.TrimEnd() == FrontmatterFence);
        if (end < 0)
        {
            return null;
        }

        return string.Join('\n', lines[1..end]);
    }
}
