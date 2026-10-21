using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Hosting;

/// <summary>What one poll listed for a project: its open issues in scope and the open PRs of each code repo.</summary>
public sealed record ProjectPoll(
    ProjectConfig Project,
    IReadOnlyList<HostIssue> OpenIssues,
    IReadOnlyDictionary<string, IReadOnlyList<HostPull>> OpenPulls)
{
    public HashSet<int> OpenIssueNumbers() =>
        OpenIssues
            .Select(issue => issue.Number)
            .ToHashSet();

    public HashSet<int> OpenPullNumbers(string repo) =>
        OpenPulls.TryGetValue(repo, out var pulls)
            ? pulls.Select(pull => pull.Number).ToHashSet()
            : [];
}

/// <summary>
/// Writes the snapshot the dashboard reads (docs/specs/dashboard.md, Snapshot). The poller hands it what it already
/// listed; only the slow refresh lists more (closed issues and PRs). It never touches a fingerprint, a planner decision
/// or the queue, and it never throws: a snapshot error is logged and the poll goes on.
/// </summary>
public sealed class SnapshotRecorder(
    ICodeHost codeHost,
    ISnapshotStore store,
    RunnerConfig config,
    TimeProvider time,
    ILogger<SnapshotRecorder> logger)
{
    private const int SlowRefreshEvery = 10;

    /// <summary>The slow refresh is due on the first poll and on every 10th one (poll numbers start at 1).</summary>
    public static bool IsRefreshDue(int pollNumber) => pollNumber == 1 || pollNumber % SlowRefreshEvery == 0;

    public void RecordIssues(ProjectConfig project, IReadOnlyList<HostIssue> issues)
    {
        try
        {
            var now = time.GetUtcNow();
            var rows = issues
                .Select(issue => SnapshotRules.IssueRow(project, issue, now))
                .ToList();

            store.SaveIssues(rows);
        }
        catch (Exception exception)
        {
            logger.LogWarning("{Project}: snapshot of the issues not saved: {Message}", project, exception.Message);
        }
    }

    public void RecordPulls(PullLocation location, IReadOnlyList<HostPull> pulls)
    {
        try
        {
            var now = time.GetUtcNow();
            var source = new PullSource(location.Project, location.Repo, now);
            var rows = pulls
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
    /// Every poll: a snapshot row that is still open but was not in this poll's open lists was merged, closed or
    /// removed from the poller's scope since the last poll. Each such issue or PR is read once, so a merge or a close
    /// shows on the dashboard within one poll, not at the next slow refresh. In the steady state nothing vanished and
    /// no call is made. Call it only after complete lists: after a failed list every row would look vanished.
    /// </summary>
    public async Task RefreshVanishedAsync(ProjectPoll poll, CancellationToken cancellationToken)
    {
        var project = poll.Project;

        try
        {
            var openIssueRows = store.ListIssues()
                .Where(row => row.IsOpen && row.Project == project.Name)
                .Select(row => (row.Repo, row.Number))
                .ToList();
            var openPullRows = store.ListPulls()
                .Where(row => row.State == SnapshotRules.Open && row.Project == project.Name)
                .Select(row => (row.Repo, row.Number))
                .ToList();

            var issuesRepo = project.IssuesRepo;
            var vanishedIssues = VanishedNumbers(openIssueRows, issuesRepo, poll.OpenIssueNumbers());
            var closedIssues = await ReadClosedAsync(
                vanishedIssues,
                number => codeHost.GetIssueAsync(issuesRepo, number, cancellationToken),
                issue => issue.IsOpen(),
                cancellationToken);

            // A quiet poll has nothing to save: no database transaction is opened for an empty list.
            if (closedIssues.Count > 0)
            {
                RecordIssues(project, closedIssues);
            }

            foreach (var repo in project.CodeRepos)
            {
                var vanishedPulls = VanishedNumbers(openPullRows, repo, poll.OpenPullNumbers(repo));
                var closedPulls = await ReadClosedAsync(
                    vanishedPulls,
                    number => codeHost.GetPullAsync(repo, number, cancellationToken),
                    pull => pull.IsOpen(),
                    cancellationToken);

                if (closedPulls.Count > 0)
                {
                    RecordPulls(new PullLocation(project, repo), closedPulls);
                }
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Project}: snapshot of vanished items not saved: {Message}", project, exception.Message);
        }
    }

    /// <summary>The open snapshot rows of one repo that this poll did not list.</summary>
    private static List<int> VanishedNumbers(
        IEnumerable<(string Repo, int Number)> openRows,
        string repo,
        IReadOnlySet<int> openNumbers) =>
        openRows
            .Where(row => string.Equals(row.Repo, repo, StringComparison.OrdinalIgnoreCase))
            .Where(row => !openNumbers.Contains(row.Number))
            .Select(row => row.Number)
            .ToList();

    /// <summary>
    /// Reads each item and returns those that are no longer open. One that is still open (only out of the poller's
    /// scope, for example its trigger label was removed) or cannot be read (deleted, VPN down) is left to the slow
    /// refresh.
    /// </summary>
    private async Task<List<T>> ReadClosedAsync<T>(
        IEnumerable<int> numbers,
        Func<int, Task<T>> read,
        Func<T, bool> isOpen,
        CancellationToken cancellationToken)
    {
        var closed = new List<T>();

        foreach (var number in numbers)
        {
            try
            {
                var item = await read(number);
                if (!isOpen(item))
                {
                    closed.Add(item);
                }
            }
            // An HTTP timeout is also an OperationCanceledException, so only a real shutdown is rethrown.
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                var kind = typeof(T).Name;
                logger.LogDebug("{Kind} #{Number} not re-read for the snapshot: {Error}", kind, number, exception.Message);
            }
        }

        return closed;
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

        var closedIssues = await codeHost.ListClosedIssuesAsync(
            repo,
            project.Settings.TriggerLabel,
            cancellationToken);

        var openNumbers = poll.OpenIssueNumbers();
        var window = new RetentionWindow(now, config.LogRetentionDays, openNumbers);
        var closedRows = closedIssues
            .Where(issue => !openNumbers.Contains(issue.Number))
            .Select(issue => SnapshotRules.IssueRow(project, issue, now))
            .ToList();
        var keptClosed = SnapshotRules.KeepClosedIssues(closedRows, window);

        store.SaveIssues(keptClosed);

        var keptNumbers = keptClosed
            .Select(row => row.Number)
            .Concat(openNumbers)
            .ToHashSet();

        store.RemoveIssuesExcept(project.Name, repo, keptNumbers);

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
        var closedPulls = await codeHost.ListPullsAsync(repo, "closed", cancellationToken);

        var now = time.GetUtcNow();
        var source = new PullSource(poll.Project, repo, now);
        var keptClosed = closedPulls
            .Select(pull => SnapshotRules.PullRow(source, pull))
            .Where(row => SnapshotRules.KeepClosedPull(row, issueKeys))
            .ToList();

        store.SavePulls(keptClosed);

        var openNumbers = poll.OpenPullNumbers(repo);
        var keptNumbers = keptClosed
            .Select(row => row.Number)
            .Concat(openNumbers)
            .ToHashSet();

        store.RemovePullsExcept(poll.Project.Name, repo, keptNumbers);
    }
}
