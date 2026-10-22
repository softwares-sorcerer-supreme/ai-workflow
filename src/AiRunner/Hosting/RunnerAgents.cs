using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

public enum AgentOutcome
{
    /// <summary>The save, revert or sync request went through.</summary>
    Done,

    /// <summary>The path is not a shared agent, skill or template, or the file does not exist.</summary>
    NotFound,

    /// <summary>The request is refused: a template, or a file git cannot restore.</summary>
    Refused,

    /// <summary>The file changed since it was read; nothing was written (a 409).</summary>
    Stale,

    /// <summary>
    /// The text failed <see cref="AgentFileRules"/>; <see cref="AgentReply.Reasons"/> says why (a 400).
    /// </summary>
    Invalid,
}

/// <summary>The answer to a save, revert or sync, with a message the page shows as it is.</summary>
public sealed record AgentReply(AgentOutcome Outcome, string Message)
{
    public IReadOnlyList<string> Reasons { get; init; } = [];
}

/// <param name="GitState">A code of <see cref="AgentStates"/>.</param>
/// <param name="SyncState">A code of <see cref="AgentStates"/>; <c>not-installed</c> for a template.</param>
/// <param name="CanRevert">Editable and different from git HEAD: Revert would change something.</param>
public sealed record AgentFileInfo(
    string Path,
    string Kind,
    string GitState,
    string SyncState,
    bool IsEditable,
    bool CanRevert);

/// <param name="BaseHash">
/// The hash of the file when the editor opened it; a different hash on disk means a stale save.
/// </param>
public sealed record AgentSave(string Path, string Text, string BaseHash);

/// <param name="Extra">Files in the Copilot home the repo does not have; never deleted.</param>
public sealed record AgentListing(IReadOnlyList<AgentFileInfo> Files, IReadOnlyList<string> Extra, bool IsOutOfSync);

/// <param name="Diff">The unified diff against git HEAD; empty when the file is unchanged.</param>
public sealed record AgentFileDetail(AgentFileInfo Info, string Text, string Hash, string Diff);

/// <summary>
/// The dashboard's way to read and change the shared agents and skills (docs/specs/dashboard-control.md section 7).
/// The web app has no file or git access of its own, so everything happens behind this interface, inside the runner.
/// </summary>
public interface IRunnerAgents
{
    Task<AgentListing> ListAsync(CancellationToken cancellationToken);

    /// <summary>Null when the path is not a shared file or the file does not exist.</summary>
    Task<AgentFileDetail?> ReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the text, writes it when the save's base hash is still the file's hash, and asks for a sync.
    /// </summary>
    Task<AgentReply> SaveAsync(AgentSave save, CancellationToken cancellationToken);

    /// <summary>Restores the file from git HEAD and asks for a sync.</summary>
    Task<AgentReply> RevertAsync(string path, CancellationToken cancellationToken);

    /// <summary>Asks for a sync between jobs; it does not wait for it.</summary>
    AgentReply RequestSync();
}

/// <summary>Edits go through one gate, so two saves of a file cannot both pass the stale-hash check.</summary>
public sealed class RunnerAgents(AgentFiles files, AgentSync sync, ILogger<RunnerAgents> logger) : IRunnerAgents
{
    private const string TemplateMessage =
        "Templates are read-only here: a template change needs its parser, its agent file and Samples.cs changed " +
        "together, which is a code change.";

    private readonly SemaphoreSlim _editGate = new(1, 1);

    public async Task<AgentListing> ListAsync(CancellationToken cancellationToken)
    {
        var paths = files.ListPaths();
        var gitStates = await files.GitStatesAsync(paths, cancellationToken);
        var drift = files.CheckDrift();

        var infos = paths
            .Select(path => InfoOf(path, gitStates[path], SyncStateIn(drift, path)))
            .ToList();

        return new AgentListing(infos, drift.Extra, drift.IsOutOfSync);
    }

    public async Task<AgentFileDetail?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var content = await files.ReadAsync(path, cancellationToken);
        if (content is null)
        {
            return null;
        }

        var gitStates = await files.GitStatesAsync([path], cancellationToken);
        var info = InfoOf(path, gitStates[path], SyncStateOf(path));
        var diff = info.GitState == AgentStates.Modified ? await files.DiffAsync(path, cancellationToken) : "";

        return new AgentFileDetail(info, content.Text, content.Hash, diff);
    }

    public async Task<AgentReply> SaveAsync(AgentSave save, CancellationToken cancellationToken)
    {
        var path = save.Path;
        var rejection = RejectionOf(path);
        if (rejection is not null)
        {
            return rejection;
        }

        await _editGate.WaitAsync(cancellationToken);
        try
        {
            var current = await files.ReadAsync(path, cancellationToken);
            if (current is null)
            {
                return new AgentReply(AgentOutcome.NotFound, "The file does not exist (no file is created here).");
            }

            if (current.Hash != save.BaseHash)
            {
                return new AgentReply(
                    AgentOutcome.Stale,
                    "The file changed since you opened it. Reload it and make your edit again.");
            }

            var reasons = AgentFileRules.Check(path, save.Text, files.AllowedModels);
            if (reasons.Count > 0)
            {
                return new AgentReply(AgentOutcome.Invalid, "The file was not saved.") { Reasons = reasons };
            }

            await files.WriteAsync(path, save.Text, cancellationToken);
        }
        finally
        {
            _editGate.Release();
        }

        logger.LogInformation("Saved {Path} from the dashboard", path);
        _ = sync.Request();

        return new AgentReply(AgentOutcome.Done, "Saved. A sync to ~/.copilot is queued for after the running job.");
    }

    public async Task<AgentReply> RevertAsync(string path, CancellationToken cancellationToken)
    {
        var rejection = RejectionOf(path);
        if (rejection is not null)
        {
            return rejection;
        }

        if (files.FileOf(path) is null)
        {
            return new AgentReply(AgentOutcome.NotFound, AgentPaths.NotSharedMessage);
        }

        await _editGate.WaitAsync(cancellationToken);
        string? problem;
        try
        {
            problem = await files.RevertAsync(path, cancellationToken);
        }
        finally
        {
            _editGate.Release();
        }

        if (problem is not null)
        {
            return new AgentReply(AgentOutcome.Refused, problem);
        }

        logger.LogInformation("Reverted {Path} to git HEAD from the dashboard", path);
        _ = sync.Request();

        return new AgentReply(
            AgentOutcome.Done,
            "Restored from git. A sync to ~/.copilot is queued for after the running job.");
    }

    public AgentReply RequestSync()
    {
        _ = sync.Request();

        return new AgentReply(AgentOutcome.Done, "Sync queued: it runs now, or right after the running job.");
    }

    /// <summary>
    /// The reply for a path the screen may not edit (not a shared file, or a template); null when it may.
    /// </summary>
    private static AgentReply? RejectionOf(string path)
    {
        if (AgentPaths.KindOf(path) is null)
        {
            return new AgentReply(AgentOutcome.NotFound, AgentPaths.NotSharedMessage);
        }

        return AgentPaths.IsEditable(path) ? null : new AgentReply(AgentOutcome.Refused, TemplateMessage);
    }

    private static string SyncStateIn(AgentDriftResult drift, string path) =>
        AgentPaths.KindOf(path) == AgentKinds.Template
            ? AgentStates.NotInstalled
            : drift.States.GetValueOrDefault(path, AgentStates.Missing);

    private string SyncStateOf(string path) =>
        AgentPaths.KindOf(path) == AgentKinds.Template ? AgentStates.NotInstalled : files.SyncStateOf(path);

    private static AgentFileInfo InfoOf(string path, string gitState, string syncState)
    {
        var kind = AgentPaths.KindOf(path)!;
        var isEditable = AgentPaths.IsEditable(path);
        var canRevert = isEditable && gitState == AgentStates.Modified;

        return new AgentFileInfo(path, kind, gitState, syncState, isEditable, canRevert);
    }
}

/// <summary>
/// The agents of a web app that runs without a runner (the OpenAPI export): nothing to show or change.
/// </summary>
public sealed class NoAgents : IRunnerAgents
{
    private static readonly AgentReply Refused =
        new(AgentOutcome.NotFound, "This dashboard has no runner to act through.");

    public static NoAgents Instance { get; } = new();

    public Task<AgentListing> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AgentListing([], [], false));

    public Task<AgentFileDetail?> ReadAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<AgentFileDetail?>(null);

    public Task<AgentReply> SaveAsync(AgentSave save, CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    public Task<AgentReply> RevertAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    public AgentReply RequestSync() => Refused;
}
