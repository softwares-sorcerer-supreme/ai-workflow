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
    /// <summary>Clone into dir if missing, else fetch all branches (with prune).</summary>
    Task CloneOrFetchAsync(string repo, string dir, CancellationToken ct);
    /// <summary>Throw away local changes, then check out startPoint (as newBranch when given, else detached).</summary>
    Task CheckoutCleanAsync(string dir, string startPoint, string? newBranch, CancellationToken ct);
    Task FetchAsync(string dir, string refspec, CancellationToken ct);
    Task<bool> RefExistsAsync(string dir, string gitRef, CancellationToken ct);
    /// <summary>The commit sha a ref points to, or null when it does not exist.</summary>
    Task<string?> ResolveAsync(string dir, string gitRef, CancellationToken ct);
    /// <summary>Number of commits in "to" that are not in "from".</summary>
    Task<int> CountCommitsAsync(string dir, string from, string to, CancellationToken ct);
    /// <summary>Distinct author e-mails of the commits in "to" that are not in "from".</summary>
    Task<IReadOnlyList<string>> AuthorsAsync(string dir, string from, string to, CancellationToken ct);
    /// <summary>git add -A, then the staged changes per file.</summary>
    Task<IReadOnlyList<DiffStat>> StageAllAsync(string dir, CancellationToken ct);
    Task CommitAsync(string dir, string message, CancellationToken ct);
    /// <summary>Push source (a ref or sha) to the branch on origin. Never forced.</summary>
    Task PushAsync(string dir, string source, string branch, CancellationToken ct);
    Task<string> DiffAsync(string dir, string baseRef, string headRef, CancellationToken ct);
    /// <summary>git add -A, then the diff of everything in the work folder (commits and uncommitted files) against baseRef.</summary>
    Task<string> StageAndDiffAsync(string dir, string baseRef, CancellationToken ct);
    Task<IReadOnlyList<string>> ChangedFilesAsync(string dir, string baseRef, string headRef, CancellationToken ct);
    /// <summary>Hash of .git/config, to notice when something in the work folder changed it.</summary>
    string ConfigFingerprint(string dir);
}

/// <summary>
/// Runs git through IProcessRunner. Credentials and hardening go in GIT_CONFIG_* environment
/// variables of each git call: the token is never written into .git/config and never shows in
/// the process list. Hooks and fsmonitor are off, so nothing the agent writes into the clone
/// (.git/hooks, a config entry) runs when the runner calls git.
/// </summary>
public sealed class GitClient(RunnerConfig config, RunnerIdentity identity, IProcessRunner processes) : IGitClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private string RemoteUrl(string repo) => $"{config.GitBucket.BaseUrl.TrimEnd('/')}/git/{repo}.git";

    public async Task CloneOrFetchAsync(string repo, string dir, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(dir, ".git")))
        {
            await GitAsync(dir, ct, "fetch", "--prune", "origin", "+refs/heads/*:refs/remotes/origin/*");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        await GitAsync(Path.GetDirectoryName(dir)!, ct, "clone", "--no-tags", RemoteUrl(repo), dir);
    }

    public async Task CheckoutCleanAsync(string dir, string startPoint, string? newBranch, CancellationToken ct)
    {
        await GitAsync(dir, ct, "reset", "--hard");
        await GitAsync(dir, ct, "clean", "-fdx");
        if (newBranch is null) await GitAsync(dir, ct, "checkout", "--detach", startPoint);
        else await GitAsync(dir, ct, "checkout", "-B", newBranch, startPoint);
    }

    public Task FetchAsync(string dir, string refspec, CancellationToken ct) => GitAsync(dir, ct, "fetch", "origin", refspec);

    public async Task<bool> RefExistsAsync(string dir, string gitRef, CancellationToken ct) =>
        await ResolveAsync(dir, gitRef, ct) is not null;

    public async Task<string?> ResolveAsync(string dir, string gitRef, CancellationToken ct)
    {
        var r = await RunAsync(dir, ct, "rev-parse", "--verify", "--quiet", gitRef + "^{commit}");
        return r.Ok ? r.StdOut.Trim() : null;
    }

    public async Task<int> CountCommitsAsync(string dir, string from, string to, CancellationToken ct) =>
        int.Parse((await GitAsync(dir, ct, "rev-list", "--count", $"{from}..{to}")).Trim());

    public async Task<IReadOnlyList<string>> AuthorsAsync(string dir, string from, string to, CancellationToken ct) =>
        (await GitAsync(dir, ct, "log", "--format=%ae", $"{from}..{to}"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<IReadOnlyList<DiffStat>> StageAllAsync(string dir, CancellationToken ct)
    {
        await GitAsync(dir, ct, "add", "-A");
        var text = await GitAsync(dir, ct, "diff", "--cached", "--numstat");
        return ParseNumstat(text);
    }

    public Task CommitAsync(string dir, string message, CancellationToken ct) =>
        GitAsync(dir, ct, "-c", $"user.name={config.Git.UserName}", "-c", $"user.email={config.Git.UserEmail}",
            "commit", "--no-verify", "-m", message);

    public Task PushAsync(string dir, string source, string branch, CancellationToken ct) =>
        GitAsync(dir, ct, "push", "origin", $"{source}:refs/heads/{branch}");

    public Task<string> DiffAsync(string dir, string baseRef, string headRef, CancellationToken ct) =>
        GitAsync(dir, ct, "diff", $"{baseRef}...{headRef}");

    public async Task<string> StageAndDiffAsync(string dir, string baseRef, CancellationToken ct)
    {
        await GitAsync(dir, ct, "add", "-A");
        return await GitAsync(dir, ct, "diff", "--cached", baseRef);
    }

    public async Task<IReadOnlyList<string>> ChangedFilesAsync(string dir, string baseRef, string headRef, CancellationToken ct) =>
        (await GitAsync(dir, ct, "diff", "--name-only", $"{baseRef}...{headRef}"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string ConfigFingerprint(string dir)
    {
        var file = Path.Combine(dir, ".git", "config");
        return File.Exists(file)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))
            : "";
    }

    /// <summary>"12\t3\tpath" lines; binary files show "-" and count as 0 lines.</summary>
    private static IReadOnlyList<DiffStat> ParseNumstat(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3)
            .Select(p => new DiffStat(p[2].Trim(), int.TryParse(p[0], out var a) ? a : 0, int.TryParse(p[1], out var d) ? d : 0))
            .ToList();

    private async Task<string> GitAsync(string dir, CancellationToken ct, params string[] args)
    {
        var r = await RunAsync(dir, ct, args);
        if (!r.Ok)
            throw new InvalidOperationException($"git {args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('='))} failed (exit {r.ExitCode}): {r.Tail(600)}");
        return r.StdOut;
    }

    private Task<ProcessResult> RunAsync(string dir, CancellationToken ct, params string[] args)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{identity.Login}:{identity.GitSecret}"));
        (string Key, string Value)[] settings =
        [
            ("http.extraHeader", $"Authorization: Basic {basic}"),
            ("credential.helper", ""),                                // never ask Windows Credential Manager
            ("core.hooksPath", Path.Combine(config.ResolvePath(config.Paths.Work), ".no-hooks")), // does not exist
            ("core.fsmonitor", "false"),
            ("core.autocrlf", "false"),
            ("core.longpaths", "true"),                               // node_modules paths pass 260 chars on Windows
        ];
        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_COUNT"] = settings.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        for (var i = 0; i < settings.Length; i++)
        {
            env[$"GIT_CONFIG_KEY_{i}"] = settings[i].Key;
            env[$"GIT_CONFIG_VALUE_{i}"] = settings[i].Value;
        }
        return processes.RunAsync(new ProcessSpec("git", args, dir, Timeout) { SetEnv = env }, ct);
    }
}
