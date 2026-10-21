namespace AiRunner.Core;

/// <summary>One note about a clone, shown inline on the Workspaces screen. The code is stable, the message is the text.</summary>
public sealed record WorkspaceWarning(string Code, string Message);

/// <summary>What <see cref="WorkspaceChecks"/> needs to know about one clone, read from disk by the caller.</summary>
/// <param name="Exists">False when the clone is not on disk yet (the next job clones it).</param>
/// <param name="ChangedPaths">Paths that differ from HEAD; there should be none between jobs.</param>
/// <param name="RepoAgentFiles">File names in the clone's .github/agents folder.</param>
/// <param name="MissingInstructions">Instruction files named in repos.yml that are not in the clone, as "component: path".</param>
public sealed record CloneFacts(
    bool Exists,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> RepoAgentFiles,
    IReadOnlyList<string> MissingInstructions);

/// <summary>
/// The warnings of a clone (docs/specs/dashboard-control.md section 6): things that surprise the owner at the next job.
/// Pure: the caller reads the disk.
/// </summary>
public static class WorkspaceChecks
{
    public const string AgentOverride = "agent-override";
    public const string InstructionsMissing = "instructions-missing";
    public const string HasChanges = "has-changes";

    private const int ListedPaths = 5;

    /// <param name="facts">What was found in the clone.</param>
    /// <param name="installedAgents">File names of the shared agents the runner installs (agents/*.agent.md).</param>
    public static IReadOnlyList<WorkspaceWarning> For(CloneFacts facts, IReadOnlyCollection<string> installedAgents)
    {
        if (!facts.Exists)
        {
            return [];
        }

        var warnings = new List<WorkspaceWarning>();

        foreach (var file in facts.RepoAgentFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            var isShared = installedAgents.Contains(file, StringComparer.OrdinalIgnoreCase);
            if (isShared)
            {
                warnings.Add(new WorkspaceWarning(
                    AgentOverride,
                    $".github/agents/{file} overrides the shared agent of the same name: the repo's copy wins."));
            }
        }

        foreach (var missing in facts.MissingInstructions)
        {
            warnings.Add(new WorkspaceWarning(
                InstructionsMissing,
                $"Instruction file not found: {missing}."));
        }

        if (facts.ChangedPaths.Count > 0)
        {
            var firstPaths = facts.ChangedPaths.Take(ListedPaths);
            var listed = string.Join(", ", firstPaths);
            var hiddenCount = facts.ChangedPaths.Count - ListedPaths;
            var more = hiddenCount > 0 ? $" and {hiddenCount} more" : "";
            warnings.Add(new WorkspaceWarning(
                HasChanges,
                $"The clone has {facts.ChangedPaths.Count} changed file(s) between jobs: {listed}{more}. " +
                "The next job throws them away."));
        }

        return warnings;
    }
}
