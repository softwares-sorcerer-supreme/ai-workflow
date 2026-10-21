using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Hosting;

public enum ActionOutcome
{
    /// <summary>The label or comment was written and the poller woken.</summary>
    Done,

    /// <summary>No such project item or no such action code.</summary>
    NotFound,

    /// <summary>The item changed since the page was drawn: the action is not offered any more.</summary>
    NotOffered,

    /// <summary>The request itself is wrong (a text is missing).</summary>
    Invalid,

    /// <summary>The code host refused or could not be reached; nothing is known to be written (a 502).</summary>
    Failed,
}

/// <summary>The answer to one action, with a message the page shows as it is.</summary>
public sealed record ActionReply(ActionOutcome Outcome, string Message);

/// <summary>Which action to run and the text the user typed for it (null when the action takes none).</summary>
public sealed record ActionCall(string Code, string? Text);

/// <summary>
/// The dashboard's way to ask for an action (docs/specs/dashboard-control.md section 4). The web app can name an
/// issue or PR and an action; it never holds a code-host client, so every read and write happens behind this interface.
/// </summary>
public interface IRunnerActions
{
    Task<ActionReply> RunIssueAsync(IssueRef issue, ActionCall call, CancellationToken cancellationToken);

    Task<ActionReply> RunPullAsync(IssueRef pull, ActionCall call, CancellationToken cancellationToken);
}

/// <summary>
/// Runs an action: reads the issue or PR fresh from the code host (never the snapshot), checks with
/// <see cref="IssueActions"/> that the action is still offered, writes the label or comment, and wakes the poller. It
/// never puts a job in the queue: the planner decides on the next poll, as it does for a change made on the code host.
/// </summary>
public sealed class RunnerActions(
    RunnerConfigSource configSource,
    ICodeHost codeHost,
    IJobReader jobs,
    RunnerIdentity identity,
    RunnerCommands commands,
    TimeProvider time,
    ILogger<RunnerActions> logger) : IRunnerActions
{
    /// <summary>The live config, read per call: each call works with the projects as they are now.</summary>
    private RunnerConfig Config => configSource.Current;

    /// <summary>One action at a time: a double click must not write twice.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<ActionReply> RunIssueAsync(IssueRef issue, ActionCall call, CancellationToken cancellationToken)
    {
        var project = Config.FindProject(issue.Repo);
        var isIssuesRepo = project?.IsIssuesRepo(issue.Repo) == true;

        return RunGuardedAsync(
            new FreshRequest(project, isIssuesRepo, issue, call, "No such issue or action."),
            RunIssueLockedAsync,
            cancellationToken);
    }

    public Task<ActionReply> RunPullAsync(IssueRef pull, ActionCall call, CancellationToken cancellationToken)
    {
        var project = Config.FindProject(pull.Repo);
        var isCodeRepo = project?.HasCodeRepo(pull.Repo) == true;

        return RunGuardedAsync(
            new FreshRequest(project, isCodeRepo, pull, call, "No such PR or action."),
            RunPullLockedAsync,
            cancellationToken);
    }

    /// <summary>The item to act on; <c>IsInProject</c> says its repo is the kind of repo this action needs.</summary>
    private sealed record FreshRequest(
        ProjectConfig? Project,
        bool IsInProject,
        IssueRef Item,
        ActionCall Call,
        string NotFoundMessage);

    /// <summary>
    /// The checks that need no code-host call, then one action at a time with code-host errors caught.
    /// </summary>
    private async Task<ActionReply> RunGuardedAsync(
        FreshRequest request,
        Func<ProjectConfig, FreshRequest, CancellationToken, Task<ActionReply>> locked,
        CancellationToken cancellationToken)
    {
        var known = IssueActions.Find(request.Call.Code);
        if (known is null || request.Project is not { } project || !request.IsInProject)
        {
            return new ActionReply(ActionOutcome.NotFound, request.NotFoundMessage);
        }

        var refusal = WhyInvalid(known, request.Call);
        if (refusal is not null)
        {
            return refusal;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await locked(project, request, cancellationToken);
        }
        catch (CodeHostException exception)
        {
            return Failure(request.Call.Code, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ActionReply> RunIssueLockedAsync(
        ProjectConfig project,
        FreshRequest request,
        CancellationToken cancellationToken)
    {
        var (_, _, item, call, _) = request;

        var host = await codeHost.GetIssueAsync(item.Repo, item.Number, cancellationToken);
        if (host.IsPullRequest())
        {
            return new ActionReply(ActionOutcome.NotFound, $"#{item.Number} is a PR, not an issue.");
        }

        var input = await ReadIssueInputAsync(project, host, cancellationToken);
        var available = IssueActions.Available(input);
        if (available.All(action => action.Code != call.Code))
        {
            return NotOffered(call.Code, item.Number, input.Waiting.Stage.Stage.Label);
        }

        var target = item;
        if (call.Code == ActionCodes.StartNextSubIssue)
        {
            // Offered means there is a sub-issue to start; the write goes on that one.
            var next = IssueActions.NextSubIssueToStart(input)!;
            target = new IssueRef(item.Repo, next.Number);
        }

        var context = new ActionWriteContext(target, call.Text, project.Settings, RunnerName);

        return await WriteAsync(context, call.Code, cancellationToken);
    }

    private async Task<ActionReply> RunPullLockedAsync(
        ProjectConfig project,
        FreshRequest request,
        CancellationToken cancellationToken)
    {
        var (_, _, item, call, _) = request;

        var pull = await codeHost.GetPullAsync(item.Repo, item.Number, cancellationToken);
        var activeJobs = jobs.ListActive();
        var hasActiveJob = activeJobs.Any(job => IsJobOf(job, item));
        var input = new PullActionInput(pull, project.Settings, identity.Login, hasActiveJob);

        var available = IssueActions.AvailableForPull(input);
        if (available.All(action => action.Code != call.Code))
        {
            return NotOffered(call.Code, item.Number, "PR");
        }

        var context = new ActionWriteContext(item, call.Text, project.Settings, RunnerName);

        return await WriteAsync(context, call.Code, cancellationToken);
    }

    /// <summary>
    /// What the code host says now about the issue, its sub-issues and the PRs of the project's code repos, shaped the
    /// way the dashboard's list builds its stages, so "offered" means the same thing here and on the page.
    /// </summary>
    private async Task<IssueActionInput> ReadIssueInputAsync(
        ProjectConfig project,
        HostIssue host,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var row = SnapshotRules.IssueRow(project, host, now);
        var subIssues = await ReadSubIssuesAsync(project, host, cancellationToken);
        var pullsByRepo = await ReadPullsAsync(project, cancellationToken);
        var activeJobs = jobs.ListActive();

        var stagingInput = new StagingInput(
            project,
            row,
            subIssues,
            pullsByRepo,
            issue => ActivityOf(issue.Repo, issue.Number, activeJobs));

        return IssueStaging.Build(stagingInput).Actions;
    }

    /// <summary>The sub-issues of a parent in build order, from the list the breakdown keeps on it.</summary>
    private async Task<IReadOnlyList<IssueSnapshot>> ReadSubIssuesAsync(
        ProjectConfig project,
        HostIssue parent,
        CancellationToken cancellationToken)
    {
        if (!parent.HasLabel(Labels.Parent))
        {
            return [];
        }

        var comments = await codeHost.ListCommentsAsync(project.IssuesRepo, parent.Number, cancellationToken);
        var numbers = SubIssueBody.FindList(comments) ?? [];
        var now = time.GetUtcNow();
        var subIssues = new List<IssueSnapshot>();

        foreach (var number in numbers)
        {
            var subIssue = await codeHost.GetIssueAsync(project.IssuesRepo, number, cancellationToken);
            subIssues.Add(SnapshotRules.IssueRow(project, subIssue, now));
        }

        return subIssues;
    }

    private async Task<Dictionary<string, IReadOnlyList<HostPull>>> ReadPullsAsync(
        ProjectConfig project,
        CancellationToken cancellationToken)
    {
        var pullsByRepo = new Dictionary<string, IReadOnlyList<HostPull>>(StringComparer.OrdinalIgnoreCase);

        foreach (var repo in project.CodeRepos)
        {
            pullsByRepo[repo] = await codeHost.ListPullsAsync(repo, "all", cancellationToken);
        }

        return pullsByRepo;
    }

    private async Task<ActionReply> WriteAsync(
        ActionWriteContext context,
        string code,
        CancellationToken cancellationToken)
    {
        var write = IssueActions.WriteOf(code, context);
        if (write is null)
        {
            return new ActionReply(ActionOutcome.Invalid, "Write a text first.");
        }

        var (repo, number) = write.Target;

        switch (write.Kind)
        {
            case ActionWriteKind.AddLabel:
                await codeHost.AddLabelsAsync(repo, number, [write.Value], cancellationToken);
                break;

            case ActionWriteKind.RemoveLabel:
                await codeHost.RemoveLabelAsync(repo, number, write.Value, cancellationToken);
                break;

            default:
                // A person's comment, not a runner comment: the planner must take it as a trigger.
                await codeHost.AddPersonCommentAsync(repo, number, write.Value, cancellationToken);
                break;
        }

        commands.WakePoller();
        logger.LogInformation("Dashboard action {Code} done on {Repo}#{Number}.", code, repo, number);

        return new ActionReply(ActionOutcome.Done, DoneMessage(write));
    }

    private string RunnerName => Config.EffectiveRunnerName();

    private static string DoneMessage(ActionWrite write)
    {
        var item = $"{write.Target.Repo}#{write.Target.Number}";

        return write.Kind switch
        {
            ActionWriteKind.AddLabel => $"Added {write.Value} to {item}. The runner polls now.",
            ActionWriteKind.RemoveLabel => $"Removed {write.Value} from {item}. The runner polls now.",
            _ => $"Comment sent on {item}. The runner polls now.",
        };
    }

    private static ActionReply? WhyInvalid(IssueAction known, ActionCall call)
    {
        if (known.NeedsText && string.IsNullOrWhiteSpace(call.Text))
        {
            return new ActionReply(ActionOutcome.Invalid, $"{known.Label} needs a text.");
        }

        return null;
    }

    private static ActionReply NotOffered(string code, int number, string state)
    {
        var label = IssueActions.Find(code)?.Label ?? code;

        return new ActionReply(
            ActionOutcome.NotOffered,
            $"\"{label}\" is not available for #{number} any more ({state}). " +
            "Reload the page to see what it can do now.");
    }

    private ActionReply Failure(string code, CodeHostException exception)
    {
        logger.LogWarning("Dashboard action {Code} failed: {Message}", code, exception.Message);

        return new ActionReply(
            ActionOutcome.Failed,
            $"The code host did not accept it ({exception.Status}). Nothing may have been written; reload to check.");
    }

    private static bool IsJobOf(JobRecord job, IssueRef item) =>
        job.Number == item.Number && string.Equals(job.Repo, item.Repo, StringComparison.OrdinalIgnoreCase);

    private static JobActivity ActivityOf(string repo, int number, IReadOnlyList<JobRecord> activeJobs)
    {
        var statuses = activeJobs
            .Where(job => IsJobOf(job, new IssueRef(repo, number)))
            .Select(job => job.Status)
            .ToList();

        return new JobActivity(statuses.Contains(JobStatus.Pending), statuses.Contains(JobStatus.Running));
    }
}
