using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

/// <summary>What a job needs from its clone.</summary>
/// <param name="StartPoint">The commit to check out, e.g. "origin/main" or a sha.</param>
/// <param name="NewBranch">Check out as this branch when given, else detached.</param>
/// <param name="ExtraFetch">Refspecs fetched first when the server has them (e.g. a PR head); a failure there is ignored.</param>
public sealed record WorkspaceRequest(
    string Repo,
    string StartPoint,
    string? NewBranch,
    IReadOnlyList<string> ExtraFetch);

public interface IWorkspace
{
    /// <summary>
    /// A clean checkout of the request's start point, with the shared templates in
    /// .ai-workflow/templates/. Returns the folder.
    /// </summary>
    Task<string> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes the clone; the next job clones again. Used when the clone cannot be trusted.</summary>
    void Discard(string repo);
}

/// <summary>
/// One persistent clone per repo in work/{owner}/{name}; jobs run one at a time, so it is
/// reset and cleaned before each job instead of cloning again. The runner's own files are
/// listed in .git/info/exclude, so "git add -A" never commits them.
/// </summary>
public sealed class Workspace(RunnerConfig config, IGitClient gitClient) : IWorkspace
{
    private const string TemplatesFolder = ".ai-workflow/templates";

    /// <summary>Files the runner writes into the work folder. Never committed.</summary>
    private static readonly string[] RunnerFiles =
        [".ai-workflow/", "context.md", "plan.md", "PR_BODY.md", "fix.md", "diff.patch", "changed-files.txt"];

    private string DirFor(string repo)
    {
        var workFolder = config.ResolvePath(config.Paths.Work);
        var repoPath = repo.Replace('/', Path.DirectorySeparatorChar);

        return Path.Combine(workFolder, repoPath);
    }

    public void Discard(string repo)
    {
        var directory = DirFor(repo);
        if (!FileSystem.DeleteDirectory(directory))
        {
            throw new IOException(
                $"Could not delete the clone of {repo} ({DirFor(repo)}); delete it by hand before the next job.");
        }
    }

    public async Task<string> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var (repo, startPoint, newBranch, extraFetch) = request;
        var directory = DirFor(repo);
        await gitClient.CloneOrFetchAsync(repo, directory, cancellationToken);

        foreach (var refspec in extraFetch)
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

        if (!await gitClient.RefExistsAsync(directory, startPoint, cancellationToken))
        {
            throw new InvalidOperationException($"'{startPoint}' does not exist in {repo}");
        }

        await gitClient.CheckoutCleanAsync(directory, startPoint, newBranch, cancellationToken);

        var excludeFile = Path.Combine(directory, ".git", "info", "exclude");
        var excludeFolder = Path.GetDirectoryName(excludeFile)!;
        Directory.CreateDirectory(excludeFolder);
        await File.WriteAllLinesAsync(excludeFile, ["# written by ai-runner", .. RunnerFiles], cancellationToken);

        var sharedTemplates = config.ResolvePath("templates");
        var cloneTemplates = Path.Combine(directory, TemplatesFolder);
        CopyDirectory(sharedTemplates, cloneTemplates);

        return directory;
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
