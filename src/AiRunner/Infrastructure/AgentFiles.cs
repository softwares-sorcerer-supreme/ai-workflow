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
public sealed class AgentFiles(RunnerConfig config, CopilotHome copilotHome, IProcessRunner processRunner)
{
    /// <summary>The folders the installer copies.</summary>
    public static readonly string[] SharedFolders = [AgentPaths.AgentsFolder, AgentPaths.SkillsFolder];

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlyDictionary<string, string> GitEnvironment = new Dictionary<string, string>
    {
        // A status that only reads must not take the index lock: a user's own git command may hold it.
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    /// <summary>Every file the dashboard shows, with "/" separators, templates included.</summary>
    public IReadOnlyList<string> ListPaths()
    {
        string[] folders = [AgentPaths.AgentsFolder, AgentPaths.SkillsFolder, AgentPaths.TemplatesFolder];

        return folders
            .SelectMany(folder => HashesOf(config.ResolvePath(folder), folder).Keys)
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

        var folder = Path.GetFullPath(config.ResolvePath(segments[0]));
        var file = Path.GetFullPath(Path.Combine(folder, Path.Combine(segments[1..])));
        var isInside = file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
        var file = FileOf(path) ?? throw new FileNotFoundException("Not a shared agent or skill file.", path);
        var current = await File.ReadAllTextAsync(file, cancellationToken);
        var hasWindowsEndings = current.Contains("\r\n", StringComparison.Ordinal);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var content = hasWindowsEndings ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : normalized;

        var temporaryFile = file + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporaryFile, content, new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryFile, file, overwrite: true);
    }

    /// <summary>Content hash of every file the installer copies, by path with "/" separators.</summary>
    public IReadOnlyDictionary<string, string> SourceHashes() => HashesOfRoot(path => config.ResolvePath(path));

    /// <summary>The same for the copy under the Copilot home.</summary>
    public IReadOnlyDictionary<string, string> InstalledHashes() =>
        HashesOfRoot(folder => Path.Combine(copilotHome.Path, folder));

    public AgentDriftResult CheckDrift() => AgentDrift.Compare(SourceHashes(), InstalledHashes());

    /// <summary>The git state of every path (<see cref="AgentStates"/>); all unknown when the folder is no git repo.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GitStatesAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken)
    {
        var states = paths.ToDictionary(path => path, _ => AgentStates.Unknown, StringComparer.Ordinal);
        string[] folders = [AgentPaths.AgentsFolder, AgentPaths.SkillsFolder, AgentPaths.TemplatesFolder];
        foreach (var folder in folders)
        {
            var directory = config.ResolvePath(folder);
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

        return (config.ResolvePath(segments[0]), string.Join('/', segments[1..]));
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

    private static IReadOnlyDictionary<string, string> HashesOfRoot(Func<string, string> folderOf)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in SharedFolders)
        {
            foreach (var (path, hash) in HashesOf(folderOf(folder), folder))
            {
                hashes[path] = hash;
            }
        }

        return hashes;
    }

    private static Dictionary<string, string> HashesOf(string directory, string folder)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            return hashes;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            hashes[folder + "/" + relativePath] = HashOf(File.ReadAllBytes(file));
        }

        return hashes;
    }

    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
