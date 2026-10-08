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

    private Poller NewPoller(bool dryRun = false) =>
        new(_kit.Config, _kit.GitBucket, _kit.State, _queue, _kit.Identity, NullLogger<Poller>.Instance) { DryRun = dryRun };

    private List<JobRequest> Pending() => _kit.State.ListJobs(JobStatus.Pending).Select(j => j.Request).ToList();

    [Fact]
    public async Task Settled_issue_is_not_read_again_but_a_label_only_change_is_seen()
    {
        _kit.GitBucket.AddIssue(7, "Export", null, "ai", Labels.QuestionsOpen);
        _kit.GitBucket.AddBotComment(7, Markers.Wrap("x", Markers.Analysis));
        var poller = NewPoller();

        await poller.PollOnceAsync(default);   // waits for @ai -> nothing to do, fingerprint saved
        await poller.PollOnceAsync(default);
        Assert.Equal(1, _kit.GitBucket.CommentListCalls);
        Assert.Empty(Pending());

        // A label is added; updated_at stays the same (GitBucket may not bump it).
        await _kit.GitBucket.AddLabelsAsync(JobTestKit.Repo, 7, [Labels.Implement], default);
        await poller.PollOnceAsync(default);

        Assert.Equal(JobType.ImplementIssue, Assert.Single(Pending()).Type);
    }

    [Fact]
    public async Task Issue_that_still_needs_work_is_planned_again_after_an_unreported_failure()
    {
        _kit.GitBucket.AddIssue(7, "Export", null, "ai");
        var poller = NewPoller();

        await poller.PollOnceAsync(default);
        Assert.True(_queue.TryDequeue(out var job));
        Assert.Equal(JobType.AnalyzeIssue, job!.Request.Type);
        _kit.State.SetJobStatus(job.Id, JobStatus.Failed, "VPN down, could not even post the failure");

        await poller.PollOnceAsync(default);   // nothing changed on the issue, but it is not forgotten

        Assert.Equal(JobType.AnalyzeIssue, Assert.Single(Pending()).Type);
    }

    [Fact]
    public async Task Dry_run_queues_nothing_and_remembers_nothing()
    {
        _kit.GitBucket.AddIssue(7, "Export", null, "ai");
        _kit.GitBucket.AddPull(3, "feature", "main");

        await NewPoller(dryRun: true).PollOnceAsync(default);

        Assert.Empty(Pending());
        Assert.Empty(_kit.State.GetIssueFingerprints(JobTestKit.Repo));
    }

    [Fact]
    public async Task Issue_assigned_to_the_runner_user_is_in_scope_without_the_label()
    {
        var issue = _kit.GitBucket.AddIssue(8, "Assigned", null);
        _kit.GitBucket.Issues[8] = issue with { Assignee = new GbUser("bot") };
        _kit.GitBucket.AddIssue(9, "Someone else's", null);

        await NewPoller().PollOnceAsync(default);

        Assert.Equal(8, Assert.Single(Pending()).Number);
    }

    [Fact]
    public async Task New_pr_head_is_queued_for_review()
    {
        _kit.GitBucket.AddPull(3, "feature", "main", sha: "sha1");
        await NewPoller().PollOnceAsync(default);

        var review = Assert.Single(Pending());
        Assert.Equal((JobType.ReviewPullRequest, "sha1"), (review.Type, review.Payload));
    }
}
