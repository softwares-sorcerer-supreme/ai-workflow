using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>One clone under work/&lt;project&gt;/, read from local git and the disk only.</summary>
/// <param name="Folder">The folder name of the clone inside the project folder (the route key of its actions).</param>
/// <param name="Head">Null when the clone is missing or has no commit.</param>
/// <param name="LastFetchAt">The time of .git/FETCH_HEAD; null when the clone never fetched.</param>
/// <param name="SizeBytes">Null until the background walk of the folder is done.</param>
public sealed record CloneInfo(
    string Repo,
    string Folder,
    bool Exists,
    IReadOnlyList<string> Components,
    CloneHead? Head,
    DateTimeOffset? LastFetchAt,
    IReadOnlyList<string> ChangedPaths,
    long? SizeBytes,
    IReadOnlyList<WorkspaceWarning> Warnings);

/// <param name="IsBusy">A job or an action holds the project right now; an action would be refused.</param>
public sealed record ProjectWorkspace(string Name, bool IsBusy, IReadOnlyList<CloneInfo> Clones);

/// <summary>A folder under work/ that belongs to no project of the config.</summary>
public sealed record OrphanFolder(string Folder, long? SizeBytes);

public sealed record WorkspaceListing(IReadOnlyList<ProjectWorkspace> Projects, IReadOnlyList<OrphanFolder> Orphans);

/// <summary>
/// Reads what is under work/ for the Workspaces screen. It never touches the network (git runs only local
/// commands) and never takes the project lock, so a screen refresh cannot disturb a job.
/// </summary>
public sealed class WorkspaceInspector(
    RunnerConfigSource configSource,
    IGitClient gitClient,
    WorkspaceLocks locks,
    DirectorySizes sizes)
{
    /// <summary>The live config, read per call: each call works with the projects as they are now.</summary>
    private RunnerConfig Config => configSource.Current;

    private const string AgentFileSuffix = ".agent.md";

    public async Task<WorkspaceListing> ListAsync(CancellationToken cancellationToken)
    {
        var installedAgents = NamesOfAgentFiles(Config.ResolvePath("agents"));
        var projects = new List<ProjectWorkspace>();

        foreach (var project in Config.Projects)
        {
            var isBusy = locks.IsHeld(project.Name);
            var clones = new List<CloneInfo>();
            foreach (var repo in project.Repos)
            {
                var clone = await InspectCloneAsync(project, repo, installedAgents, cancellationToken);
                clones.Add(clone);
            }

            projects.Add(new ProjectWorkspace(project.Name, isBusy, clones));
        }

        return new WorkspaceListing(projects, ListOrphans());
    }

    private async Task<CloneInfo> InspectCloneAsync(
        ProjectConfig project,
        string repo,
        IReadOnlyCollection<string> installedAgents,
        CancellationToken cancellationToken)
    {
        var isBusy = locks.IsHeld(project.Name);
        var directory = WorkspacePaths.CloneOf(Config, project, repo);
        var folder = ProjectConfig.FolderOf(repo);
        var components = project.Components
            .Where(component => string.Equals(component.Repo, repo, StringComparison.OrdinalIgnoreCase))
            .Select(component => component.Name)
            .ToList();

        var gitFolder = Path.Combine(directory, ".git");
        if (!Directory.Exists(gitFolder))
        {
            return new CloneInfo(repo, folder, false, components, null, null, [], null, []);
        }

        var head = await gitClient.HeadAsync(directory, cancellationToken);

        // A running job is changing the clone on purpose: its edits are no warning.
        var changedPaths = isBusy ? [] : await ChangedPathsAsync(directory, cancellationToken);

        var facts = new CloneFacts(
            true,
            changedPaths,
            NamesOfAgentFiles(Path.Combine(directory, ".github", "agents")),
            MissingInstructions(project, repo, directory));
        var warnings = WorkspaceChecks.For(facts, installedAgents);
        var size = sizes.Get(directory);

        return new CloneInfo(repo, folder, true, components, head, LastFetchOf(gitFolder), changedPaths, size, warnings);
    }

    private async Task<IReadOnlyList<string>> ChangedPathsAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            return await gitClient.ChangedPathsAsync(directory, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // A broken clone (no commit, damaged index): nothing to list; the next job clones it again if it must.
            return [];
        }
    }

    private static DateTimeOffset? LastFetchOf(string gitFolder)
    {
        var fetchFile = Path.Combine(gitFolder, "FETCH_HEAD");

        return File.Exists(fetchFile) ? new DateTimeOffset(File.GetLastWriteTimeUtc(fetchFile), TimeSpan.Zero) : null;
    }

    private static List<string> NamesOfAgentFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(folder, "*" + AgentFileSuffix)
            .Select(file => Path.GetFileName(file))
            .ToList();
    }

    /// <summary>"component: path" for every instruction file of the repo's components that is not in the clone.</summary>
    private static List<string> MissingInstructions(ProjectConfig project, string repo, string cloneFolder)
    {
        var fullClone = Path.GetFullPath(cloneFolder);
        var missing = new List<string>();

        foreach (var component in project.Components)
        {
            if (!string.Equals(component.Repo, repo, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var componentFolder = component.FolderIn(fullClone);
            foreach (var relative in component.Instructions)
            {
                var file = Path.GetFullPath(Path.Combine(componentFolder, relative));
                var isFound = GitClient.IsInside(fullClone, file) && File.Exists(file);
                if (!isFound)
                {
                    missing.Add($"{component.Name}: {relative}");
                }
            }
        }

        return missing;
    }

    private List<OrphanFolder> ListOrphans()
    {
        var workFolder = WorkspacePaths.WorkFolder(Config);
        if (!Directory.Exists(workFolder))
        {
            return [];
        }

        var projectNames = Config.Projects.Select(project => project.Name).ToList();
        var orphans = new List<OrphanFolder>();

        foreach (var directory in Directory.EnumerateDirectories(workFolder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            var isOrphan = !FileSystem.IsLink(directory) &&
                           !name.StartsWith('.') &&
                           !projectNames.Contains(name, StringComparer.OrdinalIgnoreCase);
            if (isOrphan)
            {
                orphans.Add(new OrphanFolder(name, sizes.Get(directory)));
            }
        }

        return orphans;
    }
}
