using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Jobs;

public sealed class ReviewPullRequestJobTests : IDisposable
{
    private readonly JobTestKit _kit = new(r => r.Review.SkipPaths = ["**/*.md"]);
    private ReviewPullRequestJob Job => new(_kit.Config, _kit.GitBucket, _kit.Workspace, _kit.Git, _kit.Copilot, _kit.State, _kit.Identity);

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Posts_review_then_updates_the_same_comment_on_a_new_commit()
    {
        _kit.GitBucket.AddIssue(12, "Export", null, "ai");
        _kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.Spec, Markers.Spec));
        _kit.GitBucket.AddPull(30, "ai/issue-12", "main", sha: "sha1", body: "Closes #12");
        _kit.Copilot.StdOut = "noise\n" + Markers.Wrap(Samples.Review(), Markers.Review);

        await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha1"), _kit.Run, default);

        var first = _kit.GitBucket.CommentsOf(30).Single();
        Assert.True(BotComments.Is(first, Markers.Review));
        Assert.Equal("sha1", _kit.State.GetReview(JobTestKit.Repo, 30)!.HeadSha);
        var context = File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "context.md"));
        Assert.Contains("SPEC - issue #12", context);
        Assert.Contains("stricter review", context);
        Assert.Equal("pr-review", _kit.Copilot.Requests.Single().Agent);

        _kit.GitBucket.AddPull(30, "ai/issue-12", "main", sha: "sha2", body: "Closes #12");
        await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha2"), _kit.Run, default);

        Assert.Single(_kit.GitBucket.CommentsOf(30));
        Assert.Equal(first.Id, _kit.GitBucket.Updated.Single().Id);
        Assert.Equal("sha2", _kit.State.GetReview(JobTestKit.Repo, 30)!.HeadSha);
    }

    [Fact]
    public async Task Docs_only_change_is_skipped_without_copilot()
    {
        _kit.GitBucket.AddPull(30, "docs", "main", sha: "sha1");
        _kit.Git.ChangedFiles = ["README.md", "docs/guide.md"];

        var outcome = await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha1"), _kit.Run, default);

        Assert.Equal("skipped", outcome.Result);
        Assert.Empty(_kit.Copilot.Requests);
        Assert.Equal("skipped", _kit.State.GetReview(JobTestKit.Repo, 30)!.Result);
    }

    [Fact]
    public async Task Bad_verdict_fails_without_posting()
    {
        _kit.GitBucket.AddPull(30, "feature", "main", sha: "sha1");
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(verdict: "great"), Markers.Review);

        await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha1"), _kit.Run, default));
        Assert.Empty(_kit.GitBucket.CommentsOf(30));
    }
}

public sealed class SequenceSubIssuesJobTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private SequenceSubIssuesJob Job => new(_kit.Config, _kit.GitBucket, _kit.State);

    public void Dispose() => _kit.Dispose();

    private void Arrange()
    {
        _kit.GitBucket.AddIssue(12, "Export", null, "ai", Labels.Parent, Labels.Ready, Labels.Complex);
        _kit.GitBucket.AddIssue(20, "#12.1 Entity", "Parent: #12\nOrder: 1 of 2", "ai", Labels.SubIssue, Labels.Ready, Labels.Simple, Labels.Implementing);
        _kit.GitBucket.AddIssue(21, "#12.2 Command", "Parent: #12\nOrder: 2 of 2", "ai", Labels.SubIssue, Labels.Ready, Labels.Simple);
        _kit.GitBucket.AddBotComment(12, SubIssueBody.ListMarker([20, 21]));
    }

    [Fact]
    public async Task Waits_until_the_first_sub_issue_is_merged()
    {
        Arrange();
        var outcome = await Job.RunAsync(_kit.Request(JobType.SequenceSubIssues, 12), _kit.Run, default);
        Assert.True(outcome.Quiet);
        Assert.DoesNotContain(Labels.Implement, _kit.GitBucket.LabelsOf(21));
    }

    [Fact]
    public async Task Merged_sub_issue_starts_the_next_one()
    {
        Arrange();
        _kit.GitBucket.AddPull(50, "ai/issue-12-1", "feat/issue-12", merged: true, state: "closed");

        var outcome = await Job.RunAsync(_kit.Request(JobType.SequenceSubIssues, 12), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Contains(Labels.Implement, _kit.GitBucket.LabelsOf(21));
        Assert.Single(_kit.GitBucket.CommentsOf(21));

        // next poll: already started, nothing more
        var again = await Job.RunAsync(_kit.Request(JobType.SequenceSubIssues, 12), _kit.Run, default);
        Assert.True(again.Quiet);
    }

    [Fact]
    public async Task All_merged_opens_one_final_pr()
    {
        Arrange();
        _kit.GitBucket.AddPull(50, "ai/issue-12-1", "feat/issue-12", merged: true, state: "closed", title: "#20: Entity (Copilot)");
        _kit.GitBucket.AddPull(51, "ai/issue-12-2", "feat/issue-12", merged: true, state: "closed", title: "#21: Command (Copilot)");

        await Job.RunAsync(_kit.Request(JobType.SequenceSubIssues, 12), _kit.Run, default);

        var final = _kit.GitBucket.Pulls.Single(p => p.Head.Ref == "feat/issue-12");
        Assert.Equal("main", final.Base.Ref);
        Assert.Contains("#50", final.Body);
        Assert.Contains("#51", final.Body);
        Assert.Contains("Closes #12", final.Body);

        var again = await Job.RunAsync(_kit.Request(JobType.SequenceSubIssues, 12), _kit.Run, default);
        Assert.True(again.Quiet);
        Assert.Single(_kit.GitBucket.Pulls, p => p.Head.Ref == "feat/issue-12");
    }
}
