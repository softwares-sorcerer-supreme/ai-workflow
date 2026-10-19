using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Jobs;

namespace AiRunner.Hosting;

/// <summary>What one poll listed for a project: its open issues in scope and the open PRs of each code repo.</summary>
public sealed record ProjectPoll(
    ProjectConfig Project,
    IReadOnlyList<GbIssue> OpenIssues,
    IReadOnlyDictionary<string, IReadOnlyList<GbPull>> OpenPulls);

/// <summary>
/// Writes the snapshot the dashboard reads (docs/specs/dashboard.md, Snapshot). The poller hands it what it already
/// listed; only the slow refresh lists more (closed issues and PRs). It never touches a fingerprint, a planner decision
/// or the queue, and it never throws: a snapshot error is logged and the poll goes on.
/// </summary>
public sealed class SnapshotRecorder(
    IGitBucketClient gitBucketClient,
    ISnapshotStore store,
    RunnerConfig config,
    TimeProvider time,
    ILogger<SnapshotRecorder> logger)
{
    /// <summary>The slow refresh runs on the first poll and on every poll whose number is a multiple of this.</summary>
    public const int SlowRefreshEvery = 10;

    public void RecordIssues(ProjectConfig project, IReadOnlyList<GbIssue> openIssues)
    {
        try
        {
            var now = time.GetUtcNow();
            var rows = openIssues
                .Select(issue => SnapshotRules.IssueRow(project, issue, now))
                .ToList();

            store.SaveIssues(rows);
        }
        catch (Exception exception)
        {
            logger.LogWarning("{Project}: snapshot of the issues not saved: {Message}", project, exception.Message);
        }
    }

    public void RecordPulls(PullLocation location, IReadOnlyList<GbPull> openPulls)
    {
        try
        {
            var now = time.GetUtcNow();
            var source = new PullSource(location.Project, location.Repo, now);
            var rows = openPulls
                .Select(pull => SnapshotRules.PullRow(source, pull))
                .ToList();

            store.SavePulls(rows);
        }
        catch (Exception exception)
        {
            logger.LogWarning("{Repo}: snapshot of the PRs not saved: {Message}", location.Repo, exception.Message);
        }
    }

    /// <summary>
    /// The slow refresh: keeps the closed trigger-label issues and the closed runner PRs the dashboard still needs,
    /// and removes every row that is neither open on this poll nor kept. False when it failed (try again next poll).
    /// </summary>
    public async Task<bool> RefreshClosedAsync(ProjectPoll poll, CancellationToken cancellationToken)
    {
        var project = poll.Project;

        try
        {
            var issueKeys = await RefreshClosedIssuesAsync(poll, cancellationToken);

            foreach (var repo in project.CodeRepos)
            {
                await RefreshClosedPullsAsync(poll, repo, issueKeys, cancellationToken);
            }

            logger.LogDebug("{Project}: snapshot refreshed with closed issues and PRs", project);

            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Project}: snapshot refresh failed: {Message}", project, exception.Message);

            return false;
        }
    }

    /// <summary>Saves the closed issues to keep and removes the rest; returns the keys of every issue kept.</summary>
    private async Task<HashSet<string>> RefreshClosedIssuesAsync(ProjectPoll poll, CancellationToken cancellationToken)
    {
        var project = poll.Project;
        var repo = project.IssuesRepo;
        var now = time.GetUtcNow();

        var closedIssues = await gitBucketClient.ListClosedIssuesAsync(
            repo,
            project.Settings.TriggerLabel,
            cancellationToken);

        var openNumbers = poll.OpenIssues
            .Select(issue => issue.Number)
            .ToHashSet();
        var window = new RetentionWindow(now, config.LogRetentionDays, openNumbers);
        var keptClosed = closedIssues
            .Where(issue => !openNumbers.Contains(issue.Number))
            .Select(issue => SnapshotRules.IssueRow(project, issue, now))
            .Where(row => SnapshotRules.KeepClosedIssue(row, window))
            .ToList();

        store.SaveIssues(keptClosed);

        var keptNumbers = keptClosed
            .Select(row => row.Number)
            .Concat(openNumbers)
            .ToHashSet();
        store.RemoveIssuesExcept(repo, keptNumbers);

        return keptNumbers
            .Select(number => new IssueRef(repo, number).ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task RefreshClosedPullsAsync(
        ProjectPoll poll,
        string repo,
        IReadOnlySet<string> issueKeys,
        CancellationToken cancellationToken)
    {
        var closedPulls = await gitBucketClient.ListPullsAsync(repo, "closed", cancellationToken);

        var now = time.GetUtcNow();
        var source = new PullSource(poll.Project, repo, now);
        var keptClosed = closedPulls
            .Select(pull => SnapshotRules.PullRow(source, pull))
            .Where(row => SnapshotRules.KeepClosedPull(row, issueKeys))
            .ToList();

        store.SavePulls(keptClosed);

        var openNumbers = poll.OpenPulls.TryGetValue(repo, out var openPulls)
            ? openPulls.Select(pull => pull.Number)
            : [];
        var keptNumbers = keptClosed
            .Select(row => row.Number)
            .Concat(openNumbers)
            .ToHashSet();
        store.RemovePullsExcept(repo, keptNumbers);
    }
}
