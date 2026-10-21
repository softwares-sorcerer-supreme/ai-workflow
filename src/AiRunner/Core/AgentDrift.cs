namespace AiRunner.Core;

/// <summary>Stable codes (and the text to show) for the git state and the sync state of a file.</summary>
public static class AgentStates
{
    public const string Unchanged = "unchanged";
    public const string Modified = "modified";
    public const string Untracked = "untracked";
    public const string Unknown = "unknown";

    public const string InSync = "in-sync";
    public const string Differs = "differs";
    public const string Missing = "missing";

    /// <summary>Templates are never copied to ~/.copilot/, so they have no sync state.</summary>
    public const string NotInstalled = "not-installed";

    public static string LabelOf(string code) => code switch
    {
        Unchanged => "Unchanged",
        Modified => "Modified",
        Untracked => "New (not in git)",
        Unknown => "Not in a git repo",
        InSync => "In sync",
        Differs => "Differs from ~/.copilot",
        Missing => "Not installed",
        NotInstalled => "Not installed (read-only)",
        _ => code,
    };
}

/// <param name="States">Sync state per source file (path with "/" separators).</param>
/// <param name="Extra">Files in ~/.copilot/ the repo does not have; listed, never deleted.</param>
public sealed record AgentDriftResult(IReadOnlyDictionary<string, string> States, IReadOnlyList<string> Extra)
{
    /// <summary>A file differs or is not installed: a sync would change something. Extra files do not count.</summary>
    public bool IsOutOfSync => States.Values.Any(state => state != AgentStates.InSync);
}

/// <summary>
/// Compares the shared agents and skills of this repo with the copy under ~/.copilot/ (the **Drift** of CONTEXT.md).
/// Both sides are content hashes by path, so the rule is pure and tests need no files.
/// </summary>
public static class AgentDrift
{
    public static AgentDriftResult Compare(
        IReadOnlyDictionary<string, string> sourceHashes,
        IReadOnlyDictionary<string, string> installedHashes)
    {
        var states = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, hash) in sourceHashes)
        {
            if (!installedHashes.TryGetValue(path, out var installedHash))
            {
                states[path] = AgentStates.Missing;
            }
            else
            {
                states[path] = installedHash == hash ? AgentStates.InSync : AgentStates.Differs;
            }
        }

        var extra = installedHashes.Keys
            .Where(path => !sourceHashes.ContainsKey(path))
            .Order(StringComparer.Ordinal)
            .ToList();

        return new AgentDriftResult(states, extra);
    }
}
