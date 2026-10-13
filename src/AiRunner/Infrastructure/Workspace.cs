using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>What a job needs from the project folder.</summary>
/// <param name="Project">Every repo of the project is cloned or fetched.</param>
/// <param name="TargetRepo">The repo the job works on; it is checked out at <paramref name="StartPoint"/>.</param>
/// <param name="StartPoint">The commit to check out in the target repo, e.g. "origin/main" or a sha.</param>
/// <param name="NewBranch">Check out as this branch when given, else detached.</param>
/// <param name="ExtraFetch">Refspecs fetched first in the target repo when the server has them (e.g. a PR head); a failure there is ignored.</param>
public sealed record WorkspaceRequest(
    ProjectConfig Project,
    string TargetRepo,
    string StartPoint,
    string? NewBranch,
    IReadOnlyList<string> ExtraFetch)
{
    /// <summary>The agent may change the target code (implement, review fix). False = components.md shows everything read-only.</summary>
    public bool Writable { get; init; }

    /// <summary>The one component the agent may change; null = every code component in the target repo.</summary>
    public string? TargetComponent { get; init; }
}

/// <summary>The prepared project folder: runner files go in the root, code is in one clone per repo.</summary>
/// <param name="Root">work/{project}: context.md, plan.md, .ai-workflow/ and components.md live here, outside every clone.</param>
/// <param name="Clones">Folder of the clone of each repo.</param>
public sealed record PreparedWorkspace(string Root, string TargetRepo, IReadOnlyDictionary<string, string> Clones)
{
    public string TargetClone => CloneOf(TargetRepo);

    public string CloneOf(string repo) =>
        Clones.TryGetValue(repo, out var folder)
            ? folder
            : throw new InvalidOperationException($"{repo} is not in this workspace");
}

public interface IWorkspace
{
    /// <summary>
    /// A clean checkout of every repo of the project: the target repo at the request's start point, the others
    /// detached at their default branch. The shared templates are in .ai-workflow/templates/ and components.md
    /// is written, both in the root.
    /// </summary>
    Task<PreparedWorkspace> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes one clone; the next job clones again. Used when the clone cannot be trusted.</summary>
    void Discard(ProjectConfig project, string repo);
}

/// <summary>
/// One folder per project, work/{project}, with one persistent clone per distinct repo inside (components that
/// share a repo share its clone). Jobs run one at a time, so a clone is fetched, reset and cleaned before each
/// job instead of cloned again. The runner's own files sit in the project folder, outside every clone, and are
/// deleted before each job (git clean does not reach them); each clone also lists them in .git/info/exclude,
/// so "git add -A" never commits one the agent wrote there by mistake.
/// </summary>
public sealed class Workspace(RunnerConfig config, IGitClient gitClient) : IWorkspace
{
    private const string RunnerFolder = ".ai-workflow";
    private const string TemplatesFolder = ".ai-workflow/templates";

    /// <summary>Files the runner writes into the project folder. Never committed.</summary>
    private static readonly string[] RunnerFiles =
    [
        "context.md", "plan.md", "PR_BODY.md", "fix.md", "diff.patch", "changed-files.txt", ComponentsDoc.FileName,
    ];

    private string RootOf(ProjectConfig project)
    {
        var workFolder = config.ResolvePath(config.Paths.Work);

        return Path.Combine(workFolder, project.Name);
    }

    private string CloneFolderOf(ProjectConfig project, string repo)
    {
        var root = RootOf(project);

        return Path.Combine(root, ProjectConfig.FolderOf(repo));
    }

    public void Discard(ProjectConfig project, string repo)
    {
        var directory = CloneFolderOf(project, repo);
        if (!FileSystem.DeleteDirectory(directory))
        {
            throw new IOException(
                $"Could not delete the clone of {repo} ({directory}); delete it by hand before the next job.");
        }
    }

    public async Task<PreparedWorkspace> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var project = request.Project;
        var root = RootOf(project);
        Directory.CreateDirectory(root);
        DeleteStaleRunnerFiles(root);

        var clones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in project.Repos)
        {
            var directory = CloneFolderOf(project, repo);
            await gitClient.CloneOrFetchAsync(repo, directory, cancellationToken);

            var isTarget = string.Equals(repo, request.TargetRepo, StringComparison.OrdinalIgnoreCase);
            if (isTarget)
            {
                await CheckoutTargetAsync(request, directory, cancellationToken);
            }
            else
            {
                var defaultBranch = await gitClient.DefaultBranchAsync(directory, cancellationToken);
                await gitClient.CheckoutCleanAsync(directory, defaultBranch, null, cancellationToken);
            }

            await WriteExcludeFileAsync(directory, cancellationToken);
            clones[repo] = directory;
        }

        var sharedTemplates = config.ResolvePath("templates");
        var templatesFolder = Path.Combine(root, TemplatesFolder);
        CopyDirectory(sharedTemplates, templatesFolder);

        var docInput = new ComponentsDocInput(
            project,
            root,
            request.TargetRepo,
            request.TargetComponent,
            request.Writable,
            File.Exists);
        var componentsDoc = ComponentsDoc.Build(docInput);
        var componentsFile = Path.Combine(root, ComponentsDoc.FileName);
        await File.WriteAllTextAsync(componentsFile, componentsDoc, cancellationToken);

        return new PreparedWorkspace(root, request.TargetRepo, clones);
    }

    private async Task CheckoutTargetAsync(
        WorkspaceRequest request,
        string directory,
        CancellationToken cancellationToken)
    {
        foreach (var refspec in request.ExtraFetch)
        {
            try
            {
                await gitClient.FetchAsync(directory, refspec, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Not every GitBucket version has refs/pull/*.
            }
        }

        if (!await gitClient.RefExistsAsync(directory, request.StartPoint, cancellationToken))
        {
            throw new InvalidOperationException($"'{request.StartPoint}' does not exist in {request.TargetRepo}");
        }

        await gitClient.CheckoutCleanAsync(directory, request.StartPoint, request.NewBranch, cancellationToken);
    }

    private static void DeleteStaleRunnerFiles(string root)
    {
        foreach (var name in RunnerFiles)
        {
            var file = Path.Combine(root, name);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        var runnerFolder = Path.Combine(root, RunnerFolder);
        if (!FileSystem.DeleteDirectory(runnerFolder))
        {
            throw new IOException($"Could not delete {runnerFolder}; delete it by hand before the next job.");
        }
    }

    private static async Task WriteExcludeFileAsync(string directory, CancellationToken cancellationToken)
    {
        var excludeFile = Path.Combine(directory, ".git", "info", "exclude");
        var excludeFolder = Path.GetDirectoryName(excludeFile)!;
        Directory.CreateDirectory(excludeFolder);

        string[] lines = ["# written by ai-runner", $"{RunnerFolder}/", .. RunnerFiles];
        await File.WriteAllLinesAsync(excludeFile, lines, cancellationToken);
    }

    public static void CopyDirectory(string sourceFolder, string targetFolder)
    {
        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException($"Shared folder not found: {sourceFolder}");
        }

        Directory.CreateDirectory(targetFolder);
        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceFolder, file);
            var targetFile = Path.Combine(targetFolder, relativePath);
            var targetFileFolder = Path.GetDirectoryName(targetFile)!;
            Directory.CreateDirectory(targetFileFolder);
            File.Copy(file, targetFile, overwrite: true);
        }
    }
}
