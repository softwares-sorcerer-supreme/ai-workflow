namespace AiRunner.Core;

/// <summary>What a file of the shared folders is for.</summary>
public static class AgentKinds
{
    public const string Agent = "agent";
    public const string Skill = "skill";
    public const string Template = "template";
}

/// <summary>
/// Which paths the Agents screen may name. A path comes from a request, so this is a security boundary: only the
/// shapes below pass, always with "/" as the separator, never a rooted path, a dot segment or a drive.
/// <c>agents/&lt;name&gt;.agent.md</c>, <c>skills/&lt;folder&gt;/...</c> (both editable) and
/// <c>templates/&lt;file&gt;</c> (shown read-only).
/// </summary>
public static class AgentPaths
{
    public const string AgentsFolder = "agents";
    public const string SkillsFolder = "skills";
    public const string TemplatesFolder = "templates";

    /// <summary>Every folder the Agents screen shows; the first two are what the installer copies.</summary>
    public static readonly string[] AllFolders = [AgentsFolder, SkillsFolder, TemplatesFolder];

    /// <summary>The answer for a path that is none of the shapes above.</summary>
    public const string NotSharedMessage = "Not a shared agent, skill or template file.";

    public const string AgentSuffix = ".agent.md";
    public const string SkillFile = "SKILL.md";

    /// <summary>The kind of file the path names, or null when the Agents screen must not touch it.</summary>
    public static string? KindOf(string path)
    {
        var segments = SegmentsOf(path);
        if (segments is null)
        {
            return null;
        }

        var folder = segments[0];
        var isAgent = folder == AgentsFolder
            && segments.Length == 2
            && segments[1].EndsWith(AgentSuffix, StringComparison.Ordinal)
            && segments[1].Length > AgentSuffix.Length;
        if (isAgent)
        {
            return AgentKinds.Agent;
        }

        if (folder == SkillsFolder && segments.Length >= 3)
        {
            return AgentKinds.Skill;
        }

        if (folder == TemplatesFolder && segments.Length == 2)
        {
            return AgentKinds.Template;
        }

        return null;
    }

    /// <summary>Agents and skills can be edited and reverted; templates are read-only.</summary>
    public static bool IsEditable(string path)
    {
        var kind = KindOf(path);

        return kind is AgentKinds.Agent or AgentKinds.Skill;
    }

    /// <summary>The path split at "/"; null when it is empty, rooted, has a dot, empty or odd segment.</summary>
    public static string[]? SegmentsOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260)
        {
            return null;
        }

        var segments = path.Split('/');
        if (segments.Length < 2)
        {
            return null;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        foreach (var segment in segments)
        {
            var isDotSegment = segment is "" or "." or "..";
            var hasInvalidCharacter = segment.IndexOfAny(invalidCharacters) >= 0;
            var hasTrailingSpaceOrDot = segment.EndsWith(' ') || segment.EndsWith('.');
            if (isDotSegment || hasInvalidCharacter || hasTrailingSpaceOrDot)
            {
                return null;
            }
        }

        return segments;
    }
}
