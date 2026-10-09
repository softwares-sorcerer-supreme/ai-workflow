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
