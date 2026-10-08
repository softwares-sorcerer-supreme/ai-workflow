using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

public interface IWorkspace
{
    /// <summary>
    /// A clean checkout of startPoint (e.g. "origin/main" or a sha), as newBranch when given,
    /// with the shared templates in .ai-workflow/templates/. extraFetch are refspecs fetched first
    /// when the server has them (e.g. a PR head); a failure there is ignored. Returns the folder.
    /// </summary>
    Task<string> PrepareAsync(string repo, string startPoint, string? newBranch, IReadOnlyList<string> extraFetch, CancellationToken ct);

    /// <summary>Deletes the clone; the next job clones again. Used when the clone cannot be trusted.</summary>
    void Discard(string repo);
}

/// <summary>
/// One persistent clone per repo in work/{owner}/{name}; jobs run one at a time, so it is
/// reset and cleaned before each job instead of cloning again. The runner's own files are
/// listed in .git/info/exclude, so "git add -A" never commits them.
/// </summary>
public sealed class Workspace(RunnerConfig config, IGitClient git) : IWorkspace
{
    private const string TemplatesFolder = ".ai-workflow/templates";

    /// <summary>Files the runner writes into the work folder. Never committed.</summary>
    private static readonly string[] RunnerFiles =
        [".ai-workflow/", "context.md", "plan.md", "PR_BODY.md", "fix.md", "diff.patch", "changed-files.txt",
         "review-fix.md", "REPLY.md"];

    private string DirFor(string repo) =>
        Path.Combine(config.ResolvePath(config.Paths.Work), repo.Replace('/', Path.DirectorySeparatorChar));

    public void Discard(string repo)
    {
        if (!FileSystem.DeleteDirectory(DirFor(repo)))
            throw new IOException($"Could not delete the clone of {repo} ({DirFor(repo)}); delete it by hand before the next job.");
    }

    public async Task<string> PrepareAsync(string repo, string startPoint, string? newBranch, IReadOnlyList<string> extraFetch, CancellationToken ct)
    {
        var dir = DirFor(repo);
        await git.CloneOrFetchAsync(repo, dir, ct);
        foreach (var refspec in extraFetch)
        {
            try { await git.FetchAsync(dir, refspec, ct); }
            catch (InvalidOperationException) { /* not every GitBucket version has refs/pull/* */ }
        }
        if (!await git.RefExistsAsync(dir, startPoint, ct))
            throw new InvalidOperationException($"'{startPoint}' does not exist in {repo}");
        await git.CheckoutCleanAsync(dir, startPoint, newBranch, ct);

        var exclude = Path.Combine(dir, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
        await File.WriteAllLinesAsync(exclude, ["# written by ai-runner", .. RunnerFiles], ct);

        CopyDirectory(config.ResolvePath("templates"), Path.Combine(dir, TemplatesFolder));
        return dir;
    }

    public static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from))
            throw new DirectoryNotFoundException($"Shared folder not found: {from}");
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
