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

    /// <summary>What "copilot --version" prints; null = the CLI could not be started.</summary>
    public string? Version { get; set; } = "1.0.0";

    public Task<string?> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult(Version);

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

    /// <summary>Every push with the clone it came from: lets a test see which repo got which branch.</summary>
    public List<(string Directory, string Source, string Branch)> PushLog { get; } = [];

    // A branch made by a push exists only in the clones that pushed it (each repo has its own remote);
    // refs a test sets by hand exist in every clone.
    private readonly Dictionary<string, HashSet<string>> _pushedOnlyIn = [];
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

    public string DefaultBranch { get; set; } = "origin/main";

    public Task<string> DefaultBranchAsync(string directory, CancellationToken cancellationToken) =>
        Task.FromResult(DefaultBranch);

    public Task<bool> RefExistsAsync(string directory, string gitRef, CancellationToken cancellationToken)
    {
        var refExists = Refs.ContainsKey(gitRef);
        if (refExists && _pushedOnlyIn.TryGetValue(gitRef, out var directories))
        {
            refExists = directories.Contains(directory);
        }

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
        PushLog.Add((directory, source, branch));

        var remoteRef = "origin/" + branch;
        if (!Refs.ContainsKey(remoteRef) || _pushedOnlyIn.ContainsKey(remoteRef))
        {
            if (!_pushedOnlyIn.TryGetValue(remoteRef, out var pushedIn))
            {
                pushedIn = [];
                _pushedOnlyIn[remoteRef] = pushedIn;
            }

            pushedIn.Add(directory);
        }

        Refs[remoteRef] = Refs["HEAD"];

        return Task.CompletedTask;
    }

    /// <summary>Every (base, head) range DiffAsync was asked for.</summary>
    public List<(string BaseRef, string HeadRef)> Diffs { get; } = [];

    /// <summary>What DiffAsync returns, whatever the range.</summary>
    public string DiffText { get; set; } = "diff --git a/x b/x\n";

    public Task<string> DiffAsync(string directory, string baseRef, string headRef, CancellationToken cancellationToken)
    {
        Diffs.Add((baseRef, headRef));

        return Task.FromResult(DiffText);
    }

    public List<string> StageAndDiffs { get; } = [];
    /// <summary>Null: every call returns a different diff (the agent changed code). Set it to model a round that changed nothing.</summary>
    public string? FixedDiff { get; set; }
    public Task<string> StageAndDiffAsync(string directory, string baseRef, CancellationToken cancellationToken)
    {
        StageAndDiffs.Add(baseRef);
        return Task.FromResult(FixedDiff ?? $"diff --git a/src/a.txt b/src/a.txt\n+change {StageAndDiffs.Count}\n");
    }

    /// <summary>Changed files per base ref, for a range that differs from ChangedFiles (an incremental review).</summary>
    public Dictionary<string, List<string>> ChangedFilesSince { get; } = [];

    public Task<IReadOnlyList<string>> ChangedFilesAsync(
        string directory,
        string baseRef,
        string headRef,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(ChangedFilesSince.GetValueOrDefault(baseRef) ?? ChangedFiles);

    /// <summary>Paths that differ from HEAD per clone folder, as the pretend agent left them. A restore removes them.</summary>
    public Dictionary<string, List<string>> Changed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every path the scope guard restored, with the clone it was in.</summary>
    public List<(string Directory, string Path)> Restored { get; } = [];

    public Task<IReadOnlyList<string>> ChangedPathsAsync(string directory, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> paths = Changed.TryGetValue(directory, out var changed) ? [.. changed] : [];

        return Task.FromResult(paths);
    }

    public Task RestorePathsAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            Restored.Add((directory, path));
            Changed.GetValueOrDefault(directory)?.Remove(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>What HeadAsync answers for every clone; null = no usable clone.</summary>
    public CloneHead? Head { get; set; }

    public Task<CloneHead?> HeadAsync(string directory, CancellationToken cancellationToken) =>
        Task.FromResult(Head);

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

    public List<WorkspaceRequest> Requests { get; } = [];

    public void Discard(ProjectConfig project, string repo) => Discarded.Add(repo);

    /// <summary>The root is the temp folder; each repo of the project gets a clone folder in it.</summary>
    public Task<PreparedWorkspace> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        Prepared.Add((request.StartPoint, request.NewBranch));
        Requests.Add(request);

        var clones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in request.Project.Repos)
        {
            var clone = Path.Combine(Dir, ProjectConfig.FolderOf(repo));
            Directory.CreateDirectory(clone);
            clones[repo] = clone;
        }

        return Task.FromResult(new PreparedWorkspace(Dir, request.TargetRepo, clones));
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

    /// <summary>Called with the command line of every run, to let a test model a command that writes files.</summary>
    public Action<string>? OnCommand { get; set; }

    /// <summary>Printed by every command instead of "ran {command}".</summary>
    public string? StdOut { get; set; }

    public Task<ProcessResult> RunAsync(ProcessSpec processSpec, CancellationToken cancellationToken)
    {
        var command = processSpec.RawArguments ?? string.Join(" ", processSpec.Args);
        Commands.Add(command);
        OnCommand?.Invoke(command);

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

/// <summary>A clock a test can move, for the 30 minute claim rule.</summary>
public sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Fixed, so a "day" in a test does not depend on the machine running it.</summary>
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan span) => _now += span;
}

/// <summary>Everything a job test needs, wired like Program.cs but with fakes.</summary>
public sealed class JobTestKit : IDisposable
{
    public const string Repo = "james/demo";
    public const string BaseUrl = "http://gb.test";

    public RunnerConfig Config { get; }
    public FakeCodeHost CodeHost { get; } = new();
    public FakeCopilot Copilot { get; } = new();
    public FakeGit Git { get; } = new();
    public FakeWorkspace Workspace { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public SqliteStateStore State { get; }
    public RunnerIdentity Identity { get; } = new() { Login = "bot" };

    /// <summary>The live secrets of the kit's poller and worker; a token by default, empty for setup mode.</summary>
    public RunnerCredentials Credentials { get; init; } = RunnerCredentials.Of(token: "kit-token");
    public JobRun Run { get; }

    /// <summary>The job id the kit's run writes its steps under.</summary>
    public const long StepJobId = 900;

    /// <summary>The steps the kit's run has written so far, in order.</summary>
    public IReadOnlyList<JobStepRecord> Steps => State.ListSteps(StepJobId);

    /// <summary>Closes the open step like the worker does at the end of a job.</summary>
    public void EndRun(string outcome = JobSteps.Ok, string? detail = null) => Run.EndStep(outcome, detail);

    private readonly string _tempFolder = Directory.CreateTempSubdirectory("ai-runner-kit-").FullName;

    /// <param name="configure">Changes the settings of the project.</param>
    /// <param name="project">A project of its own (see TestProjects) instead of the one-component default.</param>
    public JobTestKit(Action<ProjectSettings>? configure = null, ProjectConfig? project = null)
    {
        var component = new ComponentConfig
        {
            Name = "demo",
            Repo = Repo,
            Commands = new CommandsConfig { Build = "make build", Test = "make test" },
        };
        Project = project ?? new ProjectConfig { Name = "demo", IssuesIn = "demo", Components = [component] };
        configure?.Invoke(Project.Settings);

        Config = new RunnerConfig
        {
            CodeHost = new CodeHostConfig { BaseUrl = BaseUrl },
            Projects = [Project],
        };
        Copilot.FilesToWrite["PR_BODY.md"] = Samples.PrBody();
        var stateFile = Path.Combine(_tempFolder, "state.db");
        State = new SqliteStateStore(stateFile, pooling: false);

        var runFolder = Path.Combine(_tempFolder, "run");
        Directory.CreateDirectory(runFolder);
        Run = new JobRun(runFolder, NullLogger.Instance);
        Run.StepTracker = new JobStepTracker(StepJobId, State, TimeProvider.System, NullLogger.Instance);
        Status = new RunnerStatus("test-runner", "repos.yml", Time);
        Commands = new RunnerCommands();
    }

    /// <summary>The dashboard's runtime controls, shared by the kit's worker and poller.</summary>
    public RunnerCommands Commands { get; }

    /// <summary>The project locks the worker releases when a job ends.</summary>
    public WorkspaceLocks Locks { get; } = new();

    /// <summary>The in-memory health the poller and the worker write, on the kit's clock.</summary>
    public RunnerStatus Status { get; }

    /// <summary>The one project of the kit: one component in james/demo, which also holds the issues.</summary>
    public ProjectConfig Project { get; }

    public ProjectSettings Settings => Project.Settings;

    public ComponentConfig Component => Project.Components[0];

    public AgentSession Session => new(Config, Workspace, Git, Copilot, Processes);

    public JobLoader Loader => new(Config, CodeHost, State, Identity);

    public AttachmentFetcher Attachments => new(Config, CodeHost, Copilot);

    /// <summary>A request for an issue job (in the issues repo) or a PR job (in the first code repo).</summary>
    public JobRequest Request(JobType type, int number, string? payload = null)
    {
        var isPullJob = type is JobType.ReviewPullRequest or JobType.FixReview;
        var repo = isPullJob ? Project.CodeRepos[0] : Project.IssuesRepo;

        return new JobRequest(type, repo, number, payload);
    }

    /// <summary>A worker and its queue over this kit's state and code host, logging into a temp folder.</summary>
    public (JobWorker Worker, JobQueue Queue, string LogsDir) CreateWorker(params IJob[] jobs) =>
        CreateWorker(State, jobs);

    /// <summary>Same, but the worker writes through <paramref name="workerState"/> (the queue keeps the real store).</summary>
    public (JobWorker Worker, JobQueue Queue, string LogsDir) CreateWorker(
        IStateStore workerState,
        params IJob[] jobs)
    {
        var logsFolder = Path.Combine(_tempFolder, "logs");
        var logs = Directory.CreateDirectory(logsFolder).FullName;
        var config = new RunnerConfig
        {
            Projects = Config.Projects,
            Paths = new PathsConfig { Logs = logs },
            RunnerName = Config.RunnerName,
        };
        var queue = new JobQueue(State, NullLogger<JobQueue>.Instance);
        var worker = new JobWorker(
            config,
            queue,
            new JobFactory(jobs),
            new JobLog(config, NullLogger<JobLog>.Instance),
            new CostLog(config, State, NullLogger<CostLog>.Instance),
            workerState,
            State,
            CodeHost,
            new ClaimService(config, CodeHost, Time),
            Credentials,
            Status,
            Commands,
            Locks,
            NullLogger<JobWorker>.Instance);

        return (worker, queue, logs);
    }

    /// <summary>A poller over this kit, wired like Program.cs; the snapshot goes to the kit's state if none is given.</summary>
    public Poller NewPoller(JobQueue queue, ISnapshotStore? snapshotStore = null)
    {
        var recorder = new SnapshotRecorder(
            CodeHost,
            snapshotStore ?? State,
            Config,
            Time,
            NullLogger<SnapshotRecorder>.Instance);

        var logger = NullLogger<Poller>.Instance;

        return new Poller(
            Config,
            CodeHost,
            Loader,
            State,
            queue,
            Identity,
            Credentials,
            Status,
            recorder,
            Commands,
            new AgentFiles(Config, new CopilotHome(Path.Combine(_tempFolder, "copilot-home")), Processes),
            logger);
    }

    /// <summary>The clock of the claims and the snapshot; a test moves it to age a claim.</summary>
    public FakeTime Time { get; } = new();

    public void Dispose()
    {
        Workspace.Dispose();
        FileSystem.DeleteDirectory(_tempFolder);
    }
}
