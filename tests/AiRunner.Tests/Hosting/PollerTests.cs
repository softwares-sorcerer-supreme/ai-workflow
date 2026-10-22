using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Hosting;

public sealed class PollerTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private readonly JobQueue _queue;

    public PollerTests() => _queue = new JobQueue(_kit.State, NullLogger<JobQueue>.Instance);

    public void Dispose() => _kit.Dispose();

    private Poller NewPoller(bool dryRun = false)
    {
        var poller = _kit.NewPoller(_queue);
        poller.DryRun = dryRun;

        return poller;
    }

    private List<JobRequest> Pending() =>
        _kit.State.ListJobs(JobStatus.Pending).Select(job => job.Request).ToList();

    [Fact]
    public async Task Settled_issue_is_not_read_again_but_a_label_only_change_is_seen()
    {
        _kit.CodeHost.AddIssue(7, "Export", null, "ai", Labels.QuestionsOpen);
        var analysisComment = Markers.Wrap("x", Markers.Analysis);
        _kit.CodeHost.AddBotComment(7, analysisComment);
        var poller = NewPoller();

        await poller.PollOnceAsync(default);   // waits for @ai -> nothing to do, fingerprint saved
        await poller.PollOnceAsync(default);
        Assert.Equal(1, _kit.CodeHost.CommentListCalls);
        Assert.Empty(Pending());

        // A label is added; updated_at stays the same (GitBucket may not bump it).
        await _kit.CodeHost.AddLabelsAsync(JobTestKit.Repo, 7, [Labels.Implement], default);
        await poller.PollOnceAsync(default);

        var queuedJob = Assert.Single(Pending());
        Assert.Equal(JobType.ImplementIssue, queuedJob.Type);
    }

    [Fact]
    public async Task Issue_that_still_needs_work_is_planned_again_after_an_unreported_failure()
    {
        _kit.CodeHost.AddIssue(7, "Export", null, "ai");
        var poller = NewPoller();

        await poller.PollOnceAsync(default);

        Assert.True(_queue.TryDequeue(out var job));
        Assert.Equal(JobType.AnalyzeIssue, job!.Request.Type);
        _kit.State.SetJobStatus(job.Id, JobStatus.Failed, "VPN down, could not even post the failure");

        await poller.PollOnceAsync(default);   // nothing changed on the issue, but it is not forgotten

        var queuedJob = Assert.Single(Pending());
        Assert.Equal(JobType.AnalyzeIssue, queuedJob.Type);
    }

    [Fact]
    public async Task A_failed_poll_is_shown_in_the_runner_status_until_a_poll_goes_through()
    {
        _kit.CodeHost.IssueListError = "connection refused";
        var poller = NewPoller();

        await poller.PollOnceAsync(default);

        var failed = _kit.Status.Snapshot();
        var error = Assert.Single(failed.Errors);
        Assert.Equal("demo", error.Project);
        Assert.Contains("connection refused", error.Message);
        Assert.NotNull(failed.LastPollEndedAt);
        Assert.False(failed.IsPolling);

        _kit.CodeHost.IssueListError = null;
        await poller.PollOnceAsync(default);

        Assert.Empty(_kit.Status.Snapshot().Errors);
    }

    [Fact]
    public async Task Dry_run_queues_nothing_and_remembers_nothing()
    {
        _kit.CodeHost.AddIssue(7, "Export", null, "ai");
        _kit.CodeHost.AddPull(3, "feature", "main");

        await NewPoller(dryRun: true).PollOnceAsync(default);

        Assert.Empty(Pending());
        Assert.Empty(_kit.State.GetIssueFingerprints(JobTestKit.Repo));
    }

    [Fact]
    public async Task Issue_assigned_to_the_runner_user_is_in_scope_without_the_label()
    {
        var issue = _kit.CodeHost.AddIssue(8, "Assigned", null);
        _kit.CodeHost.Issues[8] = issue with
        {
            Assignee = new HostUser("bot")
        };
        _kit.CodeHost.AddIssue(9, "Someone else's", null);

        await NewPoller().PollOnceAsync(default);

        var queuedJob = Assert.Single(Pending());
        Assert.Equal(8, queuedJob.Number);
    }

    [Fact]
    public async Task New_head_of_a_labelled_pr_is_queued_for_review()
    {
        _kit.CodeHost.AddPull(3, "feature", "main", sha: "sha1", labels: "ai");
        _kit.CodeHost.AddPull(4, "colleague", "main", sha: "sha9");   // no label: not reviewed by default

        await NewPoller().PollOnceAsync(default);

        var review = Assert.Single(Pending());
        Assert.Equal((JobType.ReviewPullRequest, "sha1"), (review.Type, review.Payload));
    }

    [Fact]
    public async Task At_ai_on_a_pr_in_scope_queues_a_review_fix_and_other_prs_are_not_read()
    {
        _kit.CodeHost.AddPull(30, "ai/issue-12", "main", sha: "sha1", labels: "ai");
        _kit.CodeHost.AddPull(31, "feature/y", "main", sha: "sha2");   // out of scope: comments never read
        _kit.CodeHost.AddHumanComment(31, "@ai fix this");
        var trigger = _kit.CodeHost.AddHumanComment(30, "@ai 1: fix");

        await NewPoller().PollOnceAsync(default);

        var fix = Assert.Single(Pending());
        Assert.Equal((JobType.FixReview, 30, trigger.Id.ToString()), (fix.Type, fix.Number, fix.Payload));
        Assert.Equal(1, _kit.CodeHost.CommentListCalls);
    }
}
