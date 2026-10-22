using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>A file the Agents screen reads: the text as it is on disk and a hash of its bytes.</summary>
public sealed record AgentFileContent(string Text, string Hash);

/// <summary>
/// The files of agents/, skills/ and templates/ in this runner's own repo (the folders <see cref="AgentInstaller"/>
/// copies), with their git state and their drift against the Copilot home. Every path comes from a request, so it is
/// checked by <see cref="AgentPaths"/> first and the resolved file must stay inside its folder. Git runs local and
/// read-only except the revert; the runner's repo is the user's own, so no auth and no token is involved.
/// </summary>
public sealed class AgentFiles(RunnerConfigSource configSource, CopilotHome copilotHome, IProcessRunner processRunner)
{
    /// <summary>Reads only keys that need a restart (and the root folder), so the live config is always right.</summary>
    private RunnerConfig Config => configSource.Current;

    /// <summary>The models an agent file may name (empty = no check); live, so a save checks the current list.</summary>
    public IReadOnlyCollection<string> AllowedModels => Config.AllowedModels;

    /// <summary>The folders the installer copies.</summary>
    public static readonly string[] SharedFolders = AgentPaths.AllFolders[..2];

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlyDictionary<string, string> GitEnvironment = new Dictionary<string, string>
    {
        // A status that only reads must not take the index lock: a user's own git command may hold it.
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    /// <summary>Every file the dashboard shows, with "/" separators, templates included.</summary>
    public IReadOnlyList<string> ListPaths()
    {
        return AgentPaths.AllFolders
            .SelectMany(folder => PathsOf(Config.ResolvePath(folder), folder))
            .Where(path => AgentPaths.KindOf(path) is not null)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The full path of an existing managed file, or null when the path is not one or the file is missing.</summary>
    public string? FileOf(string path)
    {
        var segments = AgentPaths.SegmentsOf(path);
        if (AgentPaths.KindOf(path) is null || segments is null)
        {
            return null;
        }

        var folder = Path.GetFullPath(Config.ResolvePath(segments[0]));
        var file = Path.GetFullPath(Path.Combine(folder, Path.Combine(segments[1..])));
        var isInside = GitClient.IsInside(folder, file);

        return isInside && File.Exists(file) ? file : null;
    }

    public async Task<AgentFileContent?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var file = FileOf(path);
        if (file is null)
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(file, cancellationToken);

        return new AgentFileContent(Encoding.UTF8.GetString(bytes), HashOf(bytes));
    }

    /// <summary>
    /// Replaces an existing file (temp file in the same folder, then replace), so a crash never leaves half a file.
    /// A file with Windows line endings keeps them: a browser textarea sends "\n", and git would show every line changed.
    /// </summary>
    public async Task WriteAsync(string path, string text, CancellationToken cancellationToken)
    {
        var file = FileOf(path) ?? throw new FileNotFoundException(AgentPaths.NotSharedMessage, path);
        var current = await File.ReadAllTextAsync(file, cancellationToken);
        var hasWindowsEndings = current.Contains("\r\n", StringComparison.Ordinal);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var content = hasWindowsEndings ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : normalized;

        // Next to the repo's folders, not inside them: a sync or a drift check must never see the half-written file.
        var temporaryName = ".agent-save-" + Guid.NewGuid().ToString("N");
        var temporaryFile = Path.Combine(Config.ResolvePath("."), temporaryName);
        await File.WriteAllTextAsync(temporaryFile, content, new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryFile, file, overwrite: true);
    }

    /// <summary>Content hash of every file the installer copies, by path with "/" separators.</summary>
    private IReadOnlyDictionary<string, string> SourceHashes() =>
        HashesOfRoot(path => Config.ResolvePath(path), InstalledForm);

    /// <summary>
    /// The bytes the installer writes for a source file: the file itself, except that an agent with an
    /// <c>agent_models</c> entry gets its model line. Drift compares against this, so a model set from the config is
    /// not reported as a difference.
    /// </summary>
    private byte[] InstalledForm(string path, byte[] bytes)
    {
        const string agentsPrefix = "agents/";
        const string agentSuffix = ".agent.md";

        var isAgent = path.StartsWith(agentsPrefix, StringComparison.Ordinal) &&
                      path.EndsWith(agentSuffix, StringComparison.Ordinal);
        if (!isAgent)
        {
            return bytes;
        }

        var agent = path[agentsPrefix.Length..^agentSuffix.Length];
        if (!Config.AgentModels.TryGetValue(agent, out var model) ||
            !ModelSetting.IsAllowed(Config.AllowedModels, model))
        {
            return bytes;
        }

        var text = Encoding.UTF8.GetString(bytes);

        return Encoding.UTF8.GetBytes(AgentFrontmatter.WithModel(text, model));
    }

    /// <summary>The same for the copy under the Copilot home.</summary>
    private IReadOnlyDictionary<string, string> InstalledHashes() =>
        HashesOfRoot(folder => Path.Combine(copilotHome.Path, folder), (_, bytes) => bytes);

    /// <summary>The sync state of one file (hashes only that file and its copy).</summary>
    public string SyncStateOf(string path)
    {
        var file = FileOf(path);
        var installedFile = Path.Combine(copilotHome.Path, path);
        if (file is null || !File.Exists(installedFile))
        {
            return AgentStates.Missing;
        }

        var sourceHash = HashOf(InstalledForm(path, File.ReadAllBytes(file)));
        var installedHash = HashOf(File.ReadAllBytes(installedFile));

        return sourceHash == installedHash ? AgentStates.InSync : AgentStates.Differs;
    }

    public AgentDriftResult CheckDrift() => AgentDrift.Compare(SourceHashes(), InstalledHashes());

    /// <summary>The git state of every path (<see cref="AgentStates"/>); all unknown when the folder is no git repo.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GitStatesAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken)
    {
        var states = paths.ToDictionary(path => path, _ => AgentStates.Unknown, StringComparer.Ordinal);
        foreach (var folder in AgentPaths.AllFolders)
        {
            var directory = Config.ResolvePath(folder);
            var prefix = await RepoPrefixAsync(directory, cancellationToken);
            if (prefix is null)
            {
                continue;
            }

            var status = await GitAsync(
                directory,
                cancellationToken,
                "status",
                "--porcelain=v1",
                "-z",
                "--untracked-files=all",
                "--",
                ".");
            if (status is null || status.ExitCode != 0)
            {
                continue;
            }

            foreach (var path in paths.Where(path => path.StartsWith(folder + "/", StringComparison.Ordinal)))
            {
                states[path] = AgentStates.Unchanged;
            }

            foreach (var (changedPath, isUntracked) in ParseStatus(status.StdOut))
            {
                var isUnderPrefix = changedPath.StartsWith(prefix, StringComparison.Ordinal);
                var relativePath = isUnderPrefix ? changedPath[prefix.Length..] : changedPath;
                var path = folder + "/" + relativePath;
                if (states.ContainsKey(path))
                {
                    states[path] = isUntracked ? AgentStates.Untracked : AgentStates.Modified;
                }
            }
        }

        return states;
    }

    /// <summary>The unified diff of a file against HEAD; empty when unchanged, untracked or outside git.</summary>
    public async Task<string> DiffAsync(string path, CancellationToken cancellationToken)
    {
        var (directory, relativePath) = GitLocationOf(path);
        var result = await GitAsync(directory, cancellationToken, "diff", "--no-color", "HEAD", "--", relativePath);

        return result is { ExitCode: 0 } ? result.StdOut : "";
    }

    /// <summary>Restores the file from HEAD. Null on success, else why not (untracked file, no git).</summary>
    public async Task<string?> RevertAsync(string path, CancellationToken cancellationToken)
    {
        var (directory, relativePath) = GitLocationOf(path);
        var result = await GitAsync(directory, cancellationToken, "checkout", "HEAD", "--", relativePath);
        if (result is null)
        {
            return "git could not be started.";
        }

        if (result.ExitCode == 0)
        {
            return null;
        }

        var reason = result.StdErr.Trim();

        return reason.Length == 0 ? "git could not restore this file from HEAD." : reason;
    }

    private (string Directory, string RelativePath) GitLocationOf(string path)
    {
        var segments = AgentPaths.SegmentsOf(path)!;

        return (Config.ResolvePath(segments[0]), string.Join('/', segments[1..]));
    }

    /// <summary>The folder's path inside the repo ("" or "sub/dir/"), or null when it is not in a git repo.</summary>
    private async Task<string?> RepoPrefixAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var result = await GitAsync(directory, cancellationToken, "rev-parse", "--show-prefix");

        return result is { ExitCode: 0 } ? result.StdOut.Trim() : null;
    }

    private async Task<ProcessResult?> GitAsync(string directory, CancellationToken cancellationToken, params string[] args)
    {
        var spec = new ProcessSpec("git", args, directory, GitTimeout) { SetEnv = GitEnvironment };
        try
        {
            return await processRunner.RunAsync(spec, cancellationToken);
        }
        catch (Win32Exception)
        {
            // git is not installed: the screen shows "unknown" instead of failing.
            return null;
        }
    }

    /// <summary>The paths of <c>git status --porcelain=v1 -z</c>; a rename carries its old path as a second entry.</summary>
    private static IEnumerable<(string Path, bool IsUntracked)> ParseStatus(string output)
    {
        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4)
            {
                continue;
            }

            var code = entry[..2];
            yield return (entry[3..], code == "??");

            var isRename = code[0] is 'R' or 'C' || code[1] is 'R' or 'C';
            if (isRename)
            {
                index++;
            }
        }
    }

    private static IReadOnlyDictionary<string, string> HashesOfRoot(
        Func<string, string> folderOf,
        Func<string, byte[], byte[]> formOf)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in SharedFolders)
        {
            foreach (var (path, hash) in HashesOf(folderOf(folder), folder, formOf))
            {
                hashes[path] = hash;
            }
        }

        return hashes;
    }

    private static Dictionary<string, string> HashesOf(
        string directory,
        string folder,
        Func<string, byte[], byte[]> formOf)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, file) in FilesOf(directory, folder))
        {
            hashes[path] = HashOf(formOf(path, File.ReadAllBytes(file)));
        }

        return hashes;
    }

    private static IEnumerable<string> PathsOf(string directory, string folder) =>
        FilesOf(directory, folder).Select(entry => entry.Path);

    /// <summary>Every file under the folder as (path with "/" separators, full path); none when it is missing.</summary>
    private static IEnumerable<(string Path, string File)> FilesOf(string directory, string folder)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relativePath = System.IO.Path.GetRelativePath(directory, file).Replace('\\', '/');

            yield return (folder + "/" + relativePath, file);
        }
    }

    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
