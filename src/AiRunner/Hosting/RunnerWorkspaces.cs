using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

public enum WorkspaceOutcome
{
    /// <summary>The fetch, re-clone or delete is done.</summary>
    Done,

    /// <summary>No such project, clone or orphan folder.</summary>
    NotFound,

    /// <summary>
    /// The request is refused: not a plain folder name, a project folder, a link, a clone not made yet.
    /// </summary>
    Refused,

    /// <summary>A job (or another action) holds the project; nothing was changed (a 409).</summary>
    Busy,

    /// <summary>Git or the disk failed; the message says what (a 502).</summary>
    Failed,
}

/// <summary>The answer to one workspace action, with a message the page shows as it is.</summary>
public sealed record WorkspaceReply(WorkspaceOutcome Outcome, string Message);

/// <summary>
/// The dashboard's way to read and change the folders under work/ (docs/specs/dashboard-control.md section 6). The web
/// app has no git client, so every read and action happens behind this interface, inside the runner.
/// </summary>
public interface IRunnerWorkspaces
{
    /// <summary>Local reads only: never the network, never the project lock.</summary>
    Task<WorkspaceListing> ListAsync(CancellationToken cancellationToken);

    /// <summary>git fetch of one clone with the usual auth; the only action that uses the network.</summary>
    Task<WorkspaceReply> FetchAsync(string project, string folder, CancellationToken cancellationToken);

    /// <summary>Deletes one clone; the next job clones it again.</summary>
    Task<WorkspaceReply> RecloneAsync(string project, string folder, CancellationToken cancellationToken);

    /// <summary>Deletes a folder of work/ that belongs to no project of the config.</summary>
    Task<WorkspaceReply> DeleteOrphanAsync(string folder, CancellationToken cancellationToken);
}

/// <summary>
/// Actions on workspaces. A project action tries the project lock without waiting and answers
/// <see cref="WorkspaceOutcome.Busy"/> when a job holds it, so it can never delay or disturb a job.
/// </summary>
public sealed class RunnerWorkspaces(
    RunnerConfigSource configSource,
    WorkspaceInspector inspector,
    IGitClient gitClient,
    IWorkspace workspace,
    WorkspaceLocks locks,
    ILogger<RunnerWorkspaces> logger) : IRunnerWorkspaces
{
    /// <summary>The live config, read per call: each call works with the projects as they are now.</summary>
    private RunnerConfig Config => configSource.Current;

    private const string BusyMessage = "A job is using this project; try again when it has ended.";

    private readonly object _listingGate = new();
    private Task<WorkspaceListing>? _listing;

    /// <summary>
    /// Two open dashboard tabs, or a slow answer that overlaps the next 3 s poll, share one inspection: it spawns
    /// several git processes per clone, so a second one must not start while the first is still running.
    /// </summary>
    public Task<WorkspaceListing> ListAsync(CancellationToken cancellationToken)
    {
        lock (_listingGate)
        {
            if (_listing is null || _listing.IsCompleted)
            {
                _listing = inspector.ListAsync(CancellationToken.None);
            }

            return _listing.WaitAsync(cancellationToken);
        }
    }

    public async Task<WorkspaceReply> FetchAsync(string project, string folder, CancellationToken cancellationToken)
    {
        var target = Find(project, folder);
        if (target is null)
        {
            return new WorkspaceReply(WorkspaceOutcome.NotFound, "No such project or clone.");
        }

        using var hold = locks.TryHoldForAction(target.Project.Name);
        if (hold is null)
        {
            return new WorkspaceReply(WorkspaceOutcome.Busy, BusyMessage);
        }

        var directory = WorkspacePaths.CloneOf(Config, target.Project, target.Repo);
        if (!Directory.Exists(Path.Combine(directory, ".git")))
        {
            return new WorkspaceReply(
                WorkspaceOutcome.Refused,
                "This clone does not exist yet; the next job makes it.");
        }

        try
        {
            await gitClient.CloneOrFetchAsync(target.Repo, directory, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning("Fetch of {Repo} from the dashboard failed: {Message}", target.Repo, exception.Message);
            var fetchMessage = Text.Short(exception.Message);

            return new WorkspaceReply(WorkspaceOutcome.Failed, fetchMessage);
        }

        logger.LogInformation("{Project}: {Repo} fetched from the dashboard", target.Project.Name, target.Repo);

        return new WorkspaceReply(WorkspaceOutcome.Done, $"Fetched {target.Repo}.");
    }

    public Task<WorkspaceReply> RecloneAsync(string project, string folder, CancellationToken cancellationToken)
    {
        var target = Find(project, folder);
        if (target is null)
        {
            return Reply(WorkspaceOutcome.NotFound, "No such project or clone.");
        }

        using var hold = locks.TryHoldForAction(target.Project.Name);
        if (hold is null)
        {
            return Reply(WorkspaceOutcome.Busy, BusyMessage);
        }

        try
        {
            workspace.Discard(target.Project, target.Repo);
        }
        catch (IOException exception)
        {
            var discardMessage = Text.Short(exception.Message);

            return Reply(WorkspaceOutcome.Failed, discardMessage);
        }

        logger.LogInformation(
            "{Project}: clone of {Repo} deleted from the dashboard",
            target.Project.Name,
            target.Repo);

        return Reply(WorkspaceOutcome.Done, $"Deleted the clone of {target.Repo}; the next job clones it again.");
    }

    public Task<WorkspaceReply> DeleteOrphanAsync(string folder, CancellationToken cancellationToken)
    {
        var projectNames = Config.Projects.Select(project => project.Name).ToList();
        var reason = OrphanRules.WhyRefused(folder, projectNames);
        if (reason is not null)
        {
            return Reply(WorkspaceOutcome.Refused, reason);
        }

        // The name is plain, but the resolved path is the last word: it must stay inside work/.
        var workFolder = Path.GetFullPath(WorkspacePaths.WorkFolder(Config));
        var directory = Path.GetFullPath(Path.Combine(workFolder, folder));
        if (!GitClient.IsInside(workFolder, directory))
        {
            return Reply(WorkspaceOutcome.Refused, "The folder is not inside the work folder.");
        }

        if (!Directory.Exists(directory))
        {
            return Reply(WorkspaceOutcome.NotFound, "No such folder.");
        }

        if (FileSystem.IsLink(directory))
        {
            return Reply(WorkspaceOutcome.Refused, "The folder is a link; delete it by hand.");
        }

        // No project owns an orphan, so the lock is keyed by the folder: two deletes of it never run together, and a
        // job of a project just removed from repos.yml still holds its folder name until it ends.
        using var hold = locks.TryHoldForAction(folder);
        if (hold is null)
        {
            return Reply(WorkspaceOutcome.Busy, "This folder is in use (a running job or another delete); try later.");
        }

        if (!FileSystem.DeleteDirectory(directory))
        {
            return Reply(WorkspaceOutcome.Failed, $"Could not delete {folder} (a file may be in use).");
        }

        logger.LogInformation("Orphan folder {Folder} deleted from the dashboard", folder);

        return Reply(WorkspaceOutcome.Done, $"Deleted the folder {folder}.");
    }

    private static Task<WorkspaceReply> Reply(WorkspaceOutcome outcome, string message) =>
        Task.FromResult(new WorkspaceReply(outcome, message));

    private CloneTarget? Find(string projectName, string folder)
    {
        var project = Config.Projects.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, projectName, StringComparison.OrdinalIgnoreCase));
        var repo = project?.Repos.FirstOrDefault(candidate =>
            string.Equals(ProjectConfig.FolderOf(candidate), folder, StringComparison.OrdinalIgnoreCase));

        return project is null || repo is null ? null : new CloneTarget(project, repo);
    }

    private sealed record CloneTarget(ProjectConfig Project, string Repo);
}

/// <summary>
/// The workspaces of a web app that runs without a runner (the OpenAPI export): nothing to show or change.
/// </summary>
public sealed class NoWorkspaces : IRunnerWorkspaces
{
    private static readonly WorkspaceReply Refused =
        new(WorkspaceOutcome.NotFound, "This dashboard has no runner to act through.");

    public static NoWorkspaces Instance { get; } = new();

    public Task<WorkspaceListing> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new WorkspaceListing([], []));

    public Task<WorkspaceReply> FetchAsync(string project, string folder, CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    public Task<WorkspaceReply> RecloneAsync(string project, string folder, CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    public Task<WorkspaceReply> DeleteOrphanAsync(string folder, CancellationToken cancellationToken) =>
        Task.FromResult(Refused);
}
