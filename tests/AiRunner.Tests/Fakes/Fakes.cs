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

    public Task<ProcessResult> RunAsync(CopilotRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        foreach (var (name, content) in FilesToWrite)
            File.WriteAllText(Path.Combine(request.WorkDir, name), content);
        OnRun?.Invoke(request, Requests.Count);
        return Task.FromResult(new ProcessResult(ExitCode, StdOut, StdErr, TimeSpan.FromSeconds(1), TimedOut));
    }
}

/// <summary>Git that remembers what it was asked to do.</summary>
public sealed class FakeGit : IGitClient
{
    public HashSet<string> Refs { get; } = ["origin/main"];
    public Dictionary<string, int> CommitsAhead { get; } = [];
    /// <summary>Authors of the commits on a branch that is ahead of its base. Foreign by default.</summary>
    public List<string> Authors { get; set; } = ["other@example.com"];
    public List<DiffStat> Staged { get; set; } = [new("src/a.txt", 10, 2)];
    public List<string> ChangedFiles { get; set; } = ["src/a.txt"];
    public List<(string Source, string Branch)> Pushes { get; } = [];
    public List<string> Commits { get; } = [];

    public Task CloneOrFetchAsync(string repo, string dir, CancellationToken ct) => Task.CompletedTask;
    public Task CheckoutCleanAsync(string dir, string startPoint, string? newBranch, CancellationToken ct) => Task.CompletedTask;
    public Task FetchAsync(string dir, string refspec, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> RefExistsAsync(string dir, string gitRef, CancellationToken ct) => Task.FromResult(Refs.Contains(gitRef));
    public Task<int> CountCommitsAsync(string dir, string from, string to, CancellationToken ct) =>
        Task.FromResult(CommitsAhead.GetValueOrDefault(to));
    public Task<IReadOnlyList<string>> AuthorsAsync(string dir, string from, string to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Authors);
    public Task<IReadOnlyList<DiffStat>> StageAllAsync(string dir, CancellationToken ct) => Task.FromResult<IReadOnlyList<DiffStat>>(Staged);
    public Task CommitAsync(string dir, string message, CancellationToken ct) { Commits.Add(message); return Task.CompletedTask; }
    public Task PushAsync(string dir, string source, string branch, CancellationToken ct)
    {
        Pushes.Add((source, branch));
        Refs.Add("origin/" + branch);
        return Task.CompletedTask;
    }
    public Task<string> DiffAsync(string dir, string baseRef, string headRef, CancellationToken ct) => Task.FromResult("diff --git a/x b/x\n");
    public List<string> StageAndDiffs { get; } = [];
    /// <summary>Null: every call returns a different diff (the agent changed code). Set it to model a round that changed nothing.</summary>
    public string? FixedDiff { get; set; }
    public Task<string> StageAndDiffAsync(string dir, string baseRef, CancellationToken ct)
    {
        StageAndDiffs.Add(baseRef);
        return Task.FromResult(FixedDiff ?? $"diff --git a/src/a.txt b/src/a.txt\n+change {StageAndDiffs.Count}\n");
    }
    public Task<IReadOnlyList<string>> ChangedFilesAsync(string dir, string baseRef, string headRef, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(ChangedFiles);

    /// <summary>Returned in order; the last value repeats. Two different values = config changed.</summary>
    public Queue<string> Fingerprints { get; } = new();
    private string _lastFingerprint = "config-1";
    public string ConfigFingerprint(string dir) =>
        _lastFingerprint = Fingerprints.Count > 0 ? Fingerprints.Dequeue() : _lastFingerprint;
}

/// <summary>A temp folder instead of a clone.</summary>
public sealed class FakeWorkspace : IWorkspace, IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("ai-runner-ws-").FullName;
    public List<(string StartPoint, string? Branch)> Prepared { get; } = [];
    public List<string> Discarded { get; } = [];

    public void Discard(string repo) => Discarded.Add(repo);

    public Task<string> PrepareAsync(string repo, string startPoint, string? newBranch, IReadOnlyList<string> extraFetch, CancellationToken ct)
    {
        Prepared.Add((startPoint, newBranch));
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

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var command = spec.RawArguments ?? string.Join(" ", spec.Args);
        Commands.Add(command);
        var sequence = ExitCodeSequence.FirstOrDefault(kv => command.Contains(kv.Key)).Value;
        var code = sequence is { Count: > 0 }
            ? sequence.Count > 1 ? sequence.Dequeue() : sequence.Peek()
            : ExitCodes.FirstOrDefault(kv => command.Contains(kv.Key)).Value;
        return Task.FromResult(new ProcessResult(code, $"ran {command}", code == 0 ? "" : "1 test failed", TimeSpan.FromSeconds(1), false));
    }
}

/// <summary>Everything a job test needs, wired like Program.cs but with fakes.</summary>
public sealed class JobTestKit : IDisposable
{
    public const string Repo = "james/demo";

    public RunnerConfig Config { get; }
    public FakeGitBucket GitBucket { get; } = new();
    public FakeCopilot Copilot { get; } = new();
    public FakeGit Git { get; } = new();
    public FakeWorkspace Workspace { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public SqliteStateStore State { get; }
    public RunnerIdentity Identity { get; } = new() { Login = "bot" };
    public JobRun Run { get; }
    private readonly string _tmp = Directory.CreateTempSubdirectory("ai-runner-kit-").FullName;

    public JobTestKit(Action<RepoConfig>? configure = null)
    {
        var repo = new RepoConfig
        {
            Owner = "james", Name = "demo",
            Workflows = [.. Workflows.All],
            Commands = new CommandsConfig { Build = "make build", Test = "make test" },
        };
        configure?.Invoke(repo);
        Config = new RunnerConfig { Repos = [repo] };
        Copilot.FilesToWrite["PR_BODY.md"] = Samples.PrBody();
        State = new SqliteStateStore(Path.Combine(_tmp, "state.db"));
        var runDir = Path.Combine(_tmp, "run");
        Directory.CreateDirectory(runDir);
        Run = new JobRun(runDir, NullLogger.Instance);
    }

    public RepoConfig RepoConfig => Config.Repos[0];

    public JobRequest Request(JobType type, int number, string? payload = null) => new(type, Repo, number, payload);

    /// <summary>A worker and its queue over this kit's state and GitBucket, logging into a temp folder.</summary>
    public (JobWorker Worker, JobQueue Queue, string LogsDir) CreateWorker(params IJob[] jobs)
    {
        var logs = Directory.CreateDirectory(Path.Combine(_tmp, "logs")).FullName;
        var config = new RunnerConfig { Repos = Config.Repos, Paths = new PathsConfig { Logs = logs } };
        var queue = new JobQueue(State, NullLogger<JobQueue>.Instance);
        var worker = new JobWorker(config, queue, new JobFactory(jobs), new JobLog(config, NullLogger<JobLog>.Instance),
            new CostLog(config, State, NullLogger<CostLog>.Instance), State, GitBucket, NullLogger<JobWorker>.Instance);
        return (worker, queue, logs);
    }

    public void Dispose()
    {
        Workspace.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // release state.db so the folder can go
        FileSystem.DeleteDirectory(_tmp);
    }
}
