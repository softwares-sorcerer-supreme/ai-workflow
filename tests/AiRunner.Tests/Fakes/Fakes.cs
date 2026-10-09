using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Fakes;

/// <summary>Copilot that returns a canned answer and can "write files" into the work folder.</summary>
public sealed class FakeCopilot : ICopilotRunner
{
    public string StdOut { get; set; } = "";
    public int ExitCode { get; set; }
    public string StdErr { get; set; } = "";
    public bool TimedOut { get; set; }
    public Dictionary<string, string> FilesToWrite { get; } = [];
    public List<CopilotRequest> Requests { get; } = [];
    /// <summary>Called after FilesToWrite, with the 1-based number of the call, e.g. to write different files per round.</summary>
    public Action<CopilotRequest, int>? OnRun { get; set; }

    /// <summary>Whether the pretend CLI has --attachment.</summary>
    public bool CanAttachFiles { get; set; } = true;

    public Task<bool> CanAttachFilesAsync(string workDir, CancellationToken cancellationToken) =>
        Task.FromResult(CanAttachFiles);

    public Task<ProcessResult> RunAsync(CopilotRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        foreach (var (name, content) in FilesToWrite)
        {
            var targetFile = Path.Combine(request.WorkDir, name);
            File.WriteAllText(targetFile, content);
        }

        OnRun?.Invoke(request, Requests.Count);

        var duration = TimeSpan.FromSeconds(1);

        return Task.FromResult(new ProcessResult(ExitCode, StdOut, StdErr, duration, TimedOut));
    }
}

/// <summary>Git that remembers what it was asked to do.</summary>
public sealed class FakeGit : IGitClient
{
    /// <summary>Refs in the clone and their shas: RefExistsAsync and ResolveAsync both read this; a push updates it.</summary>
    public Dictionary<string, string> Refs { get; } = new() { ["origin/main"] = "main000", ["HEAD"] = "fix1234abcd" };
    public Dictionary<string, int> CommitsAhead { get; } = [];
    /// <summary>Authors of the commits on a branch that is ahead of its base. Foreign by default.</summary>
    public List<string> Authors { get; set; } = ["other@example.com"];
    public List<DiffStat> Staged { get; set; } = [new("src/a.txt", 10, 2)];
    public List<string> ChangedFiles { get; set; } = ["src/a.txt"];
    public List<(string Source, string Branch)> Pushes { get; } = [];
    public List<string> Commits { get; } = [];

    public Task CloneOrFetchAsync(string repo, string directory, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task CheckoutCleanAsync(
        string directory,
        string startPoint,
        string? newBranch,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FetchAsync(string directory, string refspec, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> RefExistsAsync(string directory, string gitRef, CancellationToken cancellationToken)
    {
        var refExists = Refs.ContainsKey(gitRef);

        return Task.FromResult(refExists);
    }

    public Task<string?> ResolveAsync(string directory, string gitRef, CancellationToken cancellationToken)
    {
        var sha = Refs.GetValueOrDefault(gitRef);

        return Task.FromResult(sha);
    }

    public Task<int> CountCommitsAsync(string directory, string from, string to, CancellationToken cancellationToken)
    {
        var commitCount = CommitsAhead.GetValueOrDefault(to);

        return Task.FromResult(commitCount);
    }

    public Task<IReadOnlyList<string>> AuthorsAsync(
        string directory,
        string from,
        string to,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Authors);

    public Task<IReadOnlyList<DiffStat>> StageAllAsync(string directory, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiffStat>>(Staged);

    public Task CommitAsync(string directory, string message, CancellationToken cancellationToken)
    {
        Commits.Add(message);
        return Task.CompletedTask;
    }

    /// <summary>Models a push the server rejects (the branch moved).</summary>
    public bool PushFails { get; set; }

    public Task PushAsync(string directory, string source, string branch, CancellationToken cancellationToken)
    {
        if (PushFails)
        {
            throw new InvalidOperationException("git push failed (exit 1): rejected, fetch first");
        }

        Pushes.Add((source, branch));
        Refs["origin/" + branch] = Refs["HEAD"];

        return Task.CompletedTask;
    }

    public Task<string> DiffAsync(string directory, string baseRef, string headRef, CancellationToken cancellationToken) =>
        Task.FromResult("diff --git a/x b/x\n");

    public List<string> StageAndDiffs { get; } = [];
    /// <summary>Null: every call returns a different diff (the agent changed code). Set it to model a round that changed nothing.</summary>
    public string? FixedDiff { get; set; }
    public Task<string> StageAndDiffAsync(string directory, string baseRef, CancellationToken cancellationToken)
    {
        StageAndDiffs.Add(baseRef);
        return Task.FromResult(FixedDiff ?? $"diff --git a/src/a.txt b/src/a.txt\n+change {StageAndDiffs.Count}\n");
    }

    public Task<IReadOnlyList<string>> ChangedFilesAsync(
        string directory,
        string baseRef,
        string headRef,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(ChangedFiles);

    /// <summary>Returned in order; the last value repeats. Two different values = config changed.</summary>
    public Queue<string> Fingerprints { get; } = new();

    private string _lastFingerprint = "config-1";

    public string ConfigFingerprint(string directory) =>
        _lastFingerprint = Fingerprints.Count > 0 ? Fingerprints.Dequeue() : _lastFingerprint;
}

/// <summary>A temp folder instead of a clone.</summary>
public sealed class FakeWorkspace : IWorkspace, IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("ai-runner-ws-").FullName;
    public List<(string StartPoint, string? Branch)> Prepared { get; } = [];
    public List<string> Discarded { get; } = [];

    public void Discard(string repo) => Discarded.Add(repo);

    public Task<string> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        Prepared.Add((request.StartPoint, request.NewBranch));

        return Task.FromResult(Dir);
    }

    public void Dispose() => FileSystem.DeleteDirectory(Dir);
}

/// <summary>
/// Build/test commands: exit code per command, 0 when not listed. ExitCodeSequence gives one code per
/// call instead (the last one repeats), e.g. tests that fail once and pass after a fix round.
/// </summary>
public sealed class FakeProcesses : IProcessRunner
{
    public Dictionary<string, int> ExitCodes { get; } = [];
    public Dictionary<string, Queue<int>> ExitCodeSequence { get; } = [];
    public List<string> Commands { get; } = [];

    /// <summary>Printed by every command instead of "ran {command}".</summary>
    public string? StdOut { get; set; }

    public Task<ProcessResult> RunAsync(ProcessSpec processSpec, CancellationToken cancellationToken)
    {
        var command = processSpec.RawArguments ?? string.Join(" ", processSpec.Args);
        Commands.Add(command);

        var sequence = ExitCodeSequence.FirstOrDefault(entry => command.Contains(entry.Key)).Value;
        var exitCode = sequence is { Count: > 0 }
            ? sequence.Count > 1 ? sequence.Dequeue() : sequence.Peek()
            : ExitCodes.FirstOrDefault(entry => command.Contains(entry.Key)).Value;

        var duration = TimeSpan.FromSeconds(1);

        return Task.FromResult(new ProcessResult(
            exitCode,
            StdOut ?? $"ran {command}",
            exitCode == 0 ? "" : "1 test failed",
            duration,
            false));
    }
}

/// <summary>Everything a job test needs, wired like Program.cs but with fakes.</summary>
public sealed class JobTestKit : IDisposable
{
    public const string Repo = "james/demo";
    public const string BaseUrl = "http://gb.test";

    public RunnerConfig Config { get; }
    public FakeGitBucket GitBucket { get; } = new();
    public FakeCopilot Copilot { get; } = new();
    public FakeGit Git { get; } = new();
    public FakeWorkspace Workspace { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public SqliteStateStore State { get; }
    public RunnerIdentity Identity { get; } = new() { Login = "bot" };
    public JobRun Run { get; }

    private readonly string _tempFolder = Directory.CreateTempSubdirectory("ai-runner-kit-").FullName;

    public JobTestKit(Action<RepoConfig>? configure = null)
    {
        var repo = new RepoConfig
        {
            Owner = "james",
            Name = "demo",
            Workflows = [.. Workflows.All],
            Commands = new CommandsConfig { Build = "make build", Test = "make test" },
        };
        configure?.Invoke(repo);

        Config = new RunnerConfig
        {
            GitBucket = new GitBucketConfig { BaseUrl = BaseUrl },
            Repos = [repo],
        };
        Copilot.FilesToWrite["PR_BODY.md"] = Samples.PrBody();
        var stateFile = Path.Combine(_tempFolder, "state.db");
        State = new SqliteStateStore(stateFile, pooling: false);

        var runFolder = Path.Combine(_tempFolder, "run");
        Directory.CreateDirectory(runFolder);
        Run = new JobRun(runFolder, NullLogger.Instance);
    }

    public RepoConfig RepoConfig => Config.Repos[0];

    public AgentSession Session => new(Config, Workspace, Git, Copilot, Processes);

    public JobLoader Loader => new(Config, GitBucket, State, Identity);

    public AttachmentFetcher Attachments => new(Config, GitBucket, Copilot);

    public JobRequest Request(JobType type, int number, string? payload = null) => new(type, Repo, number, payload);

    /// <summary>A worker and its queue over this kit's state and GitBucket, logging into a temp folder.</summary>
    public (JobWorker Worker, JobQueue Queue, string LogsDir) CreateWorker(params IJob[] jobs)
    {
        var logsFolder = Path.Combine(_tempFolder, "logs");
        var logs = Directory.CreateDirectory(logsFolder).FullName;
        var config = new RunnerConfig { Repos = Config.Repos, Paths = new PathsConfig { Logs = logs } };
        var queue = new JobQueue(State, NullLogger<JobQueue>.Instance);
        var worker = new JobWorker(
            config,
            queue,
            new JobFactory(jobs),
            new JobLog(config, NullLogger<JobLog>.Instance),
            new CostLog(config, State, NullLogger<CostLog>.Instance),
            State,
            GitBucket,
            NullLogger<JobWorker>.Instance);

        return (worker, queue, logs);
    }

    public void Dispose()
    {
        Workspace.Dispose();
        FileSystem.DeleteDirectory(_tempFolder);
    }
}
