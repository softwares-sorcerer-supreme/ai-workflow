using System.Text;
using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

public sealed record DiffStat(string Path, int Added, int Deleted)
{
    public int Lines => Added + Deleted;
}

/// <summary>Git commands the runner needs. The agent never runs git; the runner does.</summary>
public interface IGitClient
{
    /// <summary>Clone into directory if missing, else fetch all branches (with prune).</summary>
    Task CloneOrFetchAsync(string repo, string directory, CancellationToken cancellationToken);

    /// <summary>Throw away local changes, then check out startPoint (as newBranch when given, else detached).</summary>
    Task CheckoutCleanAsync(
        string directory,
        string startPoint,
        string? newBranch,
        CancellationToken cancellationToken);

    Task FetchAsync(string directory, string refspec, CancellationToken cancellationToken);

    /// <summary>The default branch of origin as a remote-tracking name, e.g. "origin/main".</summary>
    Task<string> DefaultBranchAsync(string directory, CancellationToken cancellationToken);

    Task<bool> RefExistsAsync(string directory, string gitRef, CancellationToken cancellationToken);

    /// <summary>The commit sha a ref points to, or null when it does not exist.</summary>
    Task<string?> ResolveAsync(string directory, string gitRef, CancellationToken cancellationToken);

    /// <summary>Number of commits in toRef that are not in fromRef.</summary>
    Task<int> CountCommitsAsync(string directory, string fromRef, string toRef, CancellationToken cancellationToken);

    /// <summary>Distinct author e-mails of the commits in toRef that are not in fromRef.</summary>
    Task<IReadOnlyList<string>> AuthorsAsync(
        string directory,
        string fromRef,
        string toRef,
        CancellationToken cancellationToken);

    /// <summary>git add -A, then the staged changes per file.</summary>
    Task<IReadOnlyList<DiffStat>> StageAllAsync(string directory, CancellationToken cancellationToken);

    Task CommitAsync(string directory, string message, CancellationToken cancellationToken);

    /// <summary>Push source (a ref or sha) to the branch on origin. Never forced.</summary>
    Task PushAsync(string directory, string source, string branch, CancellationToken cancellationToken);

    Task<string> DiffAsync(string directory, string baseRef, string headRef, CancellationToken cancellationToken);

    /// <summary>git add -A, then the diff of everything in the work folder (commits and uncommitted files) against baseRef.</summary>
    Task<string> StageAndDiffAsync(string directory, string baseRef, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ChangedFilesAsync(
        string directory,
        string baseRef,
        string headRef,
        CancellationToken cancellationToken);

    /// <summary>
    /// Paths that differ from HEAD, from the repo root: changed, added and deleted files (staged or not) and
    /// untracked files. Files that .gitignore ignores are left out.
    /// </summary>
    Task<IReadOnlyList<string>> ChangedPathsAsync(string directory, CancellationToken cancellationToken);

    /// <summary>Undoes changes: a tracked path is restored from HEAD, an untracked path is deleted.</summary>
    Task RestorePathsAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>Hash of .git/config, to notice when something in the work folder changed it.</summary>
    string ConfigFingerprint(string directory);
}

/// <summary>
/// Runs git through IProcessRunner. Credentials and hardening go in GIT_CONFIG_* environment
/// variables of each git call: the token is never written into .git/config and never shows in
/// the process list. Hooks and fsmonitor are off, so nothing the agent writes into the clone
/// (.git/hooks, a config entry) runs when the runner calls git.
/// </summary>
public sealed class GitClient(RunnerConfig config, RunnerIdentity identity, IProcessRunner processRunner) : IGitClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private string RemoteUrl(string repo) => $"{config.GitBucket.BaseUrl.TrimEnd('/')}/git/{repo}.git";

    public async Task CloneOrFetchAsync(string repo, string directory, CancellationToken cancellationToken)
    {
        var gitFolder = Path.Combine(directory, ".git");
        if (Directory.Exists(gitFolder))
        {
            await GitAsync(
                directory,
                cancellationToken,
                "fetch",
                "--prune",
                "origin",
                "+refs/heads/*:refs/remotes/origin/*");
            return;
        }

        var parentFolder = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(parentFolder);
        var remoteUrl = RemoteUrl(repo);
        await GitAsync(
            parentFolder,
            cancellationToken,
            "clone",
            "--no-tags",
            remoteUrl,
            directory);
    }

    public async Task CheckoutCleanAsync(
        string directory,
        string startPoint,
        string? newBranch,
        CancellationToken cancellationToken)
    {
        await GitAsync(directory, cancellationToken, "reset", "--hard");
        await GitAsync(directory, cancellationToken, "clean", "-fdx");

        if (newBranch is null)
        {
            await GitAsync(directory, cancellationToken, "checkout", "--detach", startPoint);
        }
        else
        {
            await GitAsync(directory, cancellationToken, "checkout", "-B", newBranch, startPoint);
        }
    }

    public Task FetchAsync(string directory, string refspec, CancellationToken cancellationToken) =>
        GitAsync(directory, cancellationToken, "fetch", "origin", refspec);

    public async Task<string> DefaultBranchAsync(string directory, CancellationToken cancellationToken)
    {
        string[] readHead = ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"];

        // Ask the server first: a clone keeps the origin/HEAD it was made with, and the default branch can move.
        // If the server cannot be asked, the local origin/HEAD below is the best guess.
        await RunAsync(directory, cancellationToken, "remote", "set-head", "origin", "--auto");
        var result = await RunAsync(directory, cancellationToken, readHead);

        if (!result.Ok)
        {
            throw new InvalidOperationException($"git could not find the default branch of origin: {result.Tail(300)}");
        }

        return result.StdOut.Trim();
    }

    public async Task<bool> RefExistsAsync(string directory, string gitRef, CancellationToken cancellationToken) =>
        await ResolveAsync(directory, gitRef, cancellationToken) is not null;

    public async Task<string?> ResolveAsync(string directory, string gitRef, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            directory,
            cancellationToken,
            "rev-parse",
            "--verify",
            "--quiet",
            gitRef + "^{commit}");

        return result.Ok ? result.StdOut.Trim() : null;
    }

    public async Task<int> CountCommitsAsync(
        string directory,
        string fromRef,
        string toRef,
        CancellationToken cancellationToken)
    {
        var output = await GitAsync(directory, cancellationToken, "rev-list", "--count", $"{fromRef}..{toRef}");
        var countText = output.Trim();

        return int.Parse(countText);
    }

    public async Task<IReadOnlyList<string>> AuthorsAsync(
        string directory,
        string fromRef,
        string toRef,
        CancellationToken cancellationToken) =>
        (await GitAsync(directory, cancellationToken, "log", "--format=%ae", $"{fromRef}..{toRef}"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<IReadOnlyList<DiffStat>> StageAllAsync(string directory, CancellationToken cancellationToken)
    {
        await GitAsync(directory, cancellationToken, "add", "-A");
        var text = await GitAsync(directory, cancellationToken, "diff", "--cached", "--numstat");

        return ParseNumstat(text);
    }

    public Task CommitAsync(string directory, string message, CancellationToken cancellationToken) =>
        GitAsync(
            directory,
            cancellationToken,
            "-c",
            $"user.name={config.Git.UserName}",
            "-c",
            $"user.email={config.Git.UserEmail}",
            "commit",
            "--no-verify",
            "-m",
            message);

    public Task PushAsync(string directory, string source, string branch, CancellationToken cancellationToken) =>
        GitAsync(directory, cancellationToken, "push", "origin", $"{source}:refs/heads/{branch}");

    public Task<string> DiffAsync(
        string directory,
        string baseRef,
        string headRef,
        CancellationToken cancellationToken) =>
        GitAsync(directory, cancellationToken, "diff", $"{baseRef}...{headRef}");

    public async Task<string> StageAndDiffAsync(string directory, string baseRef, CancellationToken cancellationToken)
    {
        await GitAsync(directory, cancellationToken, "add", "-A");

        return await GitAsync(directory, cancellationToken, "diff", "--cached", baseRef);
    }

    public async Task<IReadOnlyList<string>> ChangedFilesAsync(
        string directory,
        string baseRef,
        string headRef,
        CancellationToken cancellationToken) =>
        (await GitAsync(directory, cancellationToken, "diff", "--name-only", $"{baseRef}...{headRef}"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<IReadOnlyList<string>> ChangedPathsAsync(string directory, CancellationToken cancellationToken)
    {
        // -z: paths come NUL-separated and unquoted, so odd file names are safe. --no-renames lists the old and
        // the new path of a rename separately, so both get checked.
        var tracked = await GitAsync(
            directory,
            cancellationToken,
            "diff",
            "--name-only",
            "-z",
            "--no-renames",
            "HEAD");
        var untracked = await GitAsync(
            directory,
            cancellationToken,
            "ls-files",
            "--others",
            "--exclude-standard",
            "-z");

        return SplitNul(tracked)
            .Concat(SplitNul(untracked))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task RestorePathsAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        // Only paths inside the clone: the paths come from git, but a path that climbs out is never touched.
        var fullDirectory = Path.GetFullPath(directory);
        var safePaths = paths
            .Where(path => IsInside(fullDirectory, path))
            .ToList();
        if (safePaths.Count == 0)
        {
            return;
        }

        // Literal pathspecs: a file named "[1].txt" or ":(top)x" must not be read as a pattern.
        var inHead = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in safePaths.Chunk(100))
        {
            string[] listArguments = ["--literal-pathspecs", "ls-tree", "-r", "--name-only", "-z", "HEAD", "--", .. chunk];
            var listed = await GitAsync(directory, cancellationToken, listArguments);
            inHead.UnionWith(SplitNul(listed));
        }

        var tracked = safePaths.Where(inHead.Contains).ToList();
        foreach (var chunk in tracked.Chunk(100))
        {
            string[] restoreArguments = ["--literal-pathspecs", "checkout", "HEAD", "--", .. chunk];
            await GitAsync(directory, cancellationToken, restoreArguments);
        }

        // Not in HEAD: the agent made the file, so it goes.
        foreach (var path in safePaths.Where(path => !inHead.Contains(path)))
        {
            var file = Path.GetFullPath(Path.Combine(fullDirectory, path));
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static bool IsInside(string fullDirectory, string path)
    {
        var file = Path.GetFullPath(Path.Combine(fullDirectory, path));

        return file.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>NUL-separated git output. The process runner reads lines, so a newline can trail the last NUL.</summary>
    private static IEnumerable<string> SplitNul(string text) =>
        text.Split((char)0, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim('\r', '\n'))
            .Where(entry => entry.Length > 0);

    public string ConfigFingerprint(string directory)
    {
        var file = Path.Combine(directory, ".git", "config");

        if (!File.Exists(file))
        {
            return "";
        }

        var content = File.ReadAllBytes(file);
        var hash = System.Security.Cryptography.SHA256.HashData(content);

        return Convert.ToHexString(hash);
    }

    /// <summary>"12\t3\tpath" lines; binary files show "-" and count as 0 lines.</summary>
    private static IReadOnlyList<DiffStat> ParseNumstat(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length >= 3)
            .Select(columns => new DiffStat(
                columns[2].Trim(),
                int.TryParse(columns[0], out var added) ? added : 0,
                int.TryParse(columns[1], out var deleted) ? deleted : 0))
            .ToList();

    private async Task<string> GitAsync(string directory, CancellationToken cancellationToken, params string[] args)
    {
        var result = await RunAsync(directory, cancellationToken, args);
        if (!result.Ok)
        {
            var subcommand = args.FirstOrDefault(argument => !argument.StartsWith('-') && !argument.Contains('='));
            throw new InvalidOperationException(
                $"git {subcommand} failed (exit {result.ExitCode}): {result.Tail(600)}");
        }

        return result.StdOut;
    }

    private Task<ProcessResult> RunAsync(string directory, CancellationToken cancellationToken, params string[] args)
    {
        var credentialBytes = Encoding.UTF8.GetBytes($"{identity.Login}:{identity.GitSecret}");
        var basicCredentials = Convert.ToBase64String(credentialBytes);
        var workFolder = config.ResolvePath(config.Paths.Work);
        (string Key, string Value)[] settings =
        [
            ("http.extraHeader", $"Authorization: Basic {basicCredentials}"),
            ("credential.helper", ""),                                // never ask Windows Credential Manager
            ("core.hooksPath", Path.Combine(workFolder, ".no-hooks")), // does not exist
            ("core.fsmonitor", "false"),
            ("core.autocrlf", "false"),
            ("core.longpaths", "true"),                               // node_modules paths pass 260 chars on Windows
        ];

        var environment = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_COUNT"] = settings.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        for (var index = 0; index < settings.Length; index++)
        {
            environment[$"GIT_CONFIG_KEY_{index}"] = settings[index].Key;
            environment[$"GIT_CONFIG_VALUE_{index}"] = settings[index].Value;
        }

        return processRunner.RunAsync(
            new ProcessSpec("git", args, directory, Timeout) { SetEnv = environment },
            cancellationToken);
    }
}
