using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Hosting;

/// <summary>The snapshot the poller writes for the dashboard, and proof that it never changes what it plans.</summary>
public sealed class PollerSnapshotTests : IDisposable
{
    private readonly List<JobTestKit> _kits = [];

    public void Dispose()
    {
        foreach (var kit in _kits)
        {
            kit.Dispose();
        }
    }

    private JobTestKit NewKit(bool isReviewOn = true)
    {
        var kit = new JobTestKit(settings =>
        {
            if (!isReviewOn)
            {
                settings.Workflows = [Workflows.Analyze, Workflows.Breakdown, Workflows.Implement];
            }
        });
        _kits.Add(kit);

        return kit;
    }

    private static Poller NewPoller(JobTestKit kit, ISnapshotStore? snapshotStore = null)
    {
        var queue = new JobQueue(kit.State, NullLogger<JobQueue>.Instance);

        return kit.NewPoller(queue, snapshotStore);
    }

    private static async Task PollAsync(Poller poller, int times)
    {
        for (var poll = 0; poll < times; poll++)
        {
            await poller.PollOnceAsync(default);
        }
    }

    private static int ClosedIssueLists(JobTestKit kit) =>
        kit.GitBucket.RepoCalls.Count(call => call.Operation == "closed-issues");

    private static GbIssue Closed(JobTestKit kit, GbIssue issue, int daysAgo) =>
        kit.GitBucket.Issues[issue.Number] = issue with
        {
            State = "closed",
            UpdatedAt = kit.Time.GetUtcNow().AddDays(-daysAgo)
        };

    [Fact]
    public async Task Snapshot_holds_the_issues_in_scope_and_the_open_prs_even_with_review_and_fix_off()
    {
        var kit = NewKit(isReviewOn: false);
        kit.GitBucket.AddIssue(7, "Export", null, "ai", Labels.Ready, Labels.Simple);
        kit.GitBucket.AddIssue(9, "Not in scope", null);
        kit.GitBucket.AddPull(30, "ai/issue-7", "main", body: "Issue: james/demo#7", labels: "ai");

        await NewPoller(kit).PollOnceAsync(default);

        var issue = Assert.Single(kit.State.ListIssues());
        Assert.Equal((7, "Export", "demo", "open"), (issue.Number, issue.Title, issue.Project, issue.State));
        Assert.Contains(Labels.Simple, issue.Labels);

        var pull = Assert.Single(kit.State.ListPulls());
        Assert.Equal((30, "ai/issue-7", "james/demo#7"), (pull.Number, pull.HeadRef, pull.LinkedIssue));

        // Review and fix are off: the PR was listed for the snapshot only, never planned.
        Assert.DoesNotContain(
            kit.State.ListJobs(JobStatus.Pending),
            job => job.Request.Type is JobType.ReviewPullRequest or JobType.FixReview);
    }

    [Fact]
    public async Task Slow_refresh_runs_on_the_first_and_every_tenth_poll()
    {
        var kit = NewKit();
        kit.GitBucket.AddIssue(7, "Export", null, "ai");
        var poller = NewPoller(kit);

        await poller.PollOnceAsync(default);
        Assert.Equal(1, ClosedIssueLists(kit));

        await PollAsync(poller, 8);   // polls 2..9
        Assert.Equal(1, ClosedIssueLists(kit));

        await poller.PollOnceAsync(default);   // poll 10
        Assert.Equal(2, ClosedIssueLists(kit));
    }

    [Fact]
    public async Task Slow_refresh_keeps_recent_closed_issues_and_closed_subs_of_an_open_parent()
    {
        var kit = NewKit();
        kit.GitBucket.AddIssue(12, "Parent", null, "ai", Labels.Parent);
        var subIssueBody = DeliveryLayouts.SubIssueBodyFor(12, 1, "demo");
        Closed(kit, kit.GitBucket.AddIssue(20, "Old sub", subIssueBody, "ai", Labels.SubIssue), daysAgo: 90);
        Closed(kit, kit.GitBucket.AddIssue(8, "Old", null, "ai"), daysAgo: 90);
        Closed(kit, kit.GitBucket.AddIssue(9, "Recent", null, "ai"), daysAgo: 2);
        kit.GitBucket.AddPull(40, "ai/issue-9", "main", body: "Issue: james/demo#9", merged: true, state: "closed");
        kit.GitBucket.AddPull(41, "ai/issue-8", "main", body: "Issue: james/demo#8", merged: true, state: "closed");
        kit.GitBucket.AddPull(42, "feature/x", "main", body: "Issue: james/demo#9", state: "closed");

        await NewPoller(kit).PollOnceAsync(default);

        var issues = kit.State.ListIssues().Select(issue => issue.Number);
        Assert.Equal([9, 12, 20], issues.Order());

        var pull = Assert.Single(kit.State.ListPulls());
        Assert.Equal((40, true), (pull.Number, pull.IsMerged));
    }

    [Fact]
    public async Task Vanished_open_issue_stays_until_the_slow_refresh_removes_it()
    {
        var kit = NewKit();
        kit.GitBucket.AddIssue(5, "Gone soon", null, "ai");
        kit.GitBucket.AddPull(31, "ai/issue-5", "main", body: "Issue: james/demo#5");
        var poller = NewPoller(kit);
        await poller.PollOnceAsync(default);

        // Deleted on the server, or its label was removed: it is not listed any more.
        kit.GitBucket.Issues.Remove(5);
        kit.GitBucket.Pulls.Clear();
        await PollAsync(poller, 8);   // polls 2..9

        Assert.Single(kit.State.ListIssues());
        Assert.Single(kit.State.ListPulls());

        await poller.PollOnceAsync(default);   // poll 10

        Assert.Empty(kit.State.ListIssues());
        Assert.Empty(kit.State.ListPulls());
    }

    [Fact]
    public async Task A_failed_issue_list_leaves_the_refresh_due_for_the_next_poll()
    {
        var kit = NewKit();
        kit.GitBucket.AddIssue(7, "Export", null, "ai");
        var poller = NewPoller(kit);

        kit.GitBucket.IssueListError = "VPN down";
        await poller.PollOnceAsync(default);
        Assert.Equal(0, ClosedIssueLists(kit));

        kit.GitBucket.IssueListError = null;
        await poller.PollOnceAsync(default);
        Assert.Equal(1, ClosedIssueLists(kit));
    }

    /// <summary>
    /// Three runners over the same GitBucket data: no snapshot at all (the baseline), a working snapshot, and a
    /// snapshot whose every write fails. All three must plan, queue and remember exactly the same.
    /// </summary>
    [Fact]
    public async Task Writing_the_snapshot_or_failing_to_changes_no_decision_no_fingerprint_and_no_poll_status()
    {
        var baseline = NewKit();
        var working = NewKit();
        var broken = NewKit();
        foreach (var kit in new[] { baseline, working, broken })
        {
            kit.GitBucket.AddIssue(7, "Export", null, "ai");
            var waiting = kit.GitBucket.AddIssue(8, "Waiting", null, "ai", Labels.QuestionsOpen);

            // The same updated_at in both kits, so their fingerprints can be compared.
            kit.GitBucket.Issues[8] = waiting with
            {
                UpdatedAt = kit.Time.GetUtcNow()
            };
            kit.GitBucket.AddBotComment(8, Markers.Wrap("x", Markers.Analysis));
            kit.GitBucket.AddPull(30, "feature", "main", sha: "sha1", labels: "ai");
        }

        await PollAsync(NewPoller(baseline, new NoSnapshotStore()), 2);
        await PollAsync(NewPoller(working), 2);
        await PollAsync(NewPoller(broken, new ThrowingSnapshotStore()), 2);

        static List<(JobType, string, int, string?)> Planned(JobTestKit kit) =>
            kit.State.ListJobs(JobStatus.Pending)
                .Select(job => (job.Request.Type, job.Request.Repo, job.Request.Number, job.Request.Payload))
                .ToList();

        List<(JobType, string, int, string?)> expected =
        [
            (JobType.AnalyzeIssue, JobTestKit.Repo, 7, null),
            (JobType.ReviewPullRequest, JobTestKit.Repo, 30, "sha1"),
        ];
        Assert.Equal(expected, Planned(baseline));
        Assert.Equal(Planned(baseline), Planned(working));
        Assert.Equal(Planned(baseline), Planned(broken));

        var baselineFingerprints = baseline.State.GetIssueFingerprints(JobTestKit.Repo);
        Assert.NotEmpty(baselineFingerprints);
        Assert.Equal(baselineFingerprints, working.State.GetIssueFingerprints(JobTestKit.Repo));
        Assert.Equal(baselineFingerprints, broken.State.GetIssueFingerprints(JobTestKit.Repo));

        Assert.Empty(working.Status.Snapshot().Errors);
        Assert.Empty(broken.Status.Snapshot().Errors);
        Assert.NotEmpty(working.State.ListIssues());
        Assert.Empty(baseline.State.ListIssues());
    }

    [Fact]
    public async Task Slow_refresh_of_one_project_leaves_the_rows_of_another_project_in_the_same_repo()
    {
        var kit = NewKit();
        kit.State.SavePulls(
        [
            new PullSnapshot("james/demo", 77, "other", "t", "open", false, "x", "main", "s", default, default),
        ]);
        kit.State.SaveIssues(
        [
            new IssueSnapshot("james/demo", 77, "other", "t", "open", [], [], default, default),
        ]);

        await NewPoller(kit).PollOnceAsync(default);   // poll 1: slow refresh of project "demo"

        Assert.Equal("other", Assert.Single(kit.State.ListPulls()).Project);
        Assert.Equal("other", Assert.Single(kit.State.ListIssues()).Project);
    }

    [Fact]
    public async Task Dry_run_writes_no_snapshot_and_lists_no_extra_prs()
    {
        var kit = NewKit(isReviewOn: false);
        kit.GitBucket.AddIssue(7, "Export", null, "ai");
        kit.GitBucket.AddPull(30, "ai/issue-7", "main");
        var poller = NewPoller(kit);
        poller.DryRun = true;

        await poller.PollOnceAsync(default);

        Assert.Empty(kit.State.ListIssues());
        Assert.Empty(kit.GitBucket.ReposOf("pulls"));
        Assert.Equal(0, ClosedIssueLists(kit));
    }

    /// <summary>No snapshot at all: every write is dropped, the way the runner was before the dashboard.</summary>
    private sealed class NoSnapshotStore : ISnapshotStore
    {
        public void SaveIssues(IReadOnlyList<IssueSnapshot> issues)
        {
            // Nothing is kept.
        }

        public void SavePulls(IReadOnlyList<PullSnapshot> pulls)
        {
            // Nothing is kept.
        }

        public void RemoveIssuesExcept(string project, string repo, IReadOnlySet<int> keep)
        {
            // Nothing to remove.
        }

        public void RemovePullsExcept(string project, string repo, IReadOnlySet<int> keep)
        {
            // Nothing to remove.
        }

        public IReadOnlyList<IssueSnapshot> ListIssues() => [];

        public IReadOnlyList<PullSnapshot> ListPulls() => [];
    }

    /// <summary>A snapshot store whose every call fails, like a locked or broken database.</summary>
    private sealed class ThrowingSnapshotStore : ISnapshotStore
    {
        public void SaveIssues(IReadOnlyList<IssueSnapshot> issues) => throw DiskFull();

        public void SavePulls(IReadOnlyList<PullSnapshot> pulls) => throw DiskFull();

        public void RemoveIssuesExcept(string project, string repo, IReadOnlySet<int> keep) => throw DiskFull();

        public void RemovePullsExcept(string project, string repo, IReadOnlySet<int> keep) => throw DiskFull();

        public IReadOnlyList<IssueSnapshot> ListIssues() => throw DiskFull();

        public IReadOnlyList<PullSnapshot> ListPulls() => throw DiskFull();

        private static InvalidOperationException DiskFull() => new("disk full");
    }
}
