using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Jobs;

public sealed class ReviewPullRequestJobTests : IDisposable
{
    private readonly JobTestKit _kit = new(repo => repo.Review.SkipPaths = ["**/*.md"]);
    private ReviewPullRequestJob Job =>
        new(_kit.Config, _kit.GitBucket, _kit.Loader, _kit.Workspace, _kit.Git, _kit.Copilot, _kit.State, _kit.Attachments);

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Posts_review_then_updates_the_same_comment_on_a_new_commit()
    {
        _kit.GitBucket.AddIssue(12, "Export", null, "ai");
        var specComment = Markers.Wrap(Samples.Spec, Markers.Spec);
        _kit.GitBucket.AddBotComment(12, specComment);
        _kit.GitBucket.AddPull(30, "ai/issue-12", "main", sha: "sha1", body: "Closes #12", labels: "ai");
        var reviewText = Samples.Review();
        _kit.Copilot.StdOut = "noise\n" + Markers.Wrap(reviewText, Markers.Review);

        var firstReviewRequest = _kit.Request(JobType.ReviewPullRequest, 30, "sha1");
        var firstOutcome = await Job.RunAsync(firstReviewRequest, _kit.Run, default);

        Assert.Equal(
            "needs changes (full, review 1 of 3): 1 bug, 0 risk, 0 convention, 1 style, 0 still open",
            firstOutcome.Detail);
        Assert.Equal(1, firstOutcome.CopilotCalls);

        var first = _kit.GitBucket.CommentsOf(30).Single();
        Assert.True(BotComments.Is(first, Markers.Review));
        Assert.Equal("sha1", _kit.State.GetReview(JobTestKit.Repo, 30)!.HeadSha);

        var contextFile = Path.Combine(_kit.Workspace.Dir, "context.md");
        var context = File.ReadAllText(contextFile);
        Assert.Contains("SPEC - issue #12", context);
        Assert.Contains("stricter review", context);
        Assert.Equal("pr-review", _kit.Copilot.Requests.Single().Agent);
        Assert.Contains("AGENTS.md in this folder", _kit.Copilot.Requests.Single().Prompt);
        Assert.Null(_kit.Workspace.Requests.Single().OthersAt);

        _kit.GitBucket.AddPull(30, "ai/issue-12", "main", sha: "sha2", body: "Closes #12", labels: "ai");
        var secondReviewRequest = _kit.Request(JobType.ReviewPullRequest, 30, "sha2");
        await Job.RunAsync(secondReviewRequest, _kit.Run, default);

        Assert.Single(_kit.GitBucket.CommentsOf(30));
        Assert.Equal(first.Id, _kit.GitBucket.Updated.Single().Id);
        Assert.Equal("sha2", _kit.State.GetReview(JobTestKit.Repo, 30)!.HeadSha);
        // sha1 is not in the clone (a force push), so the second review reads the whole PR again.
        Assert.Equal(("origin/main", "sha2"), _kit.Git.Diffs.Last());
    }

    private const string PreviousRows = """
        | # | Status | Where | Problem |
        | --- | --- | --- | --- |
        | P1 | still open | `a.cs:4` | Null check missing |
        """;

    /// <summary>A PR at sha1 that had its first AI review, and a new commit sha2 on top of it.</summary>
    private async Task<JobOutcome> ReviewTwiceAsync(int commitsOfSha1NotInSha2 = 0)
    {
        _kit.GitBucket.AddPull(30, "ai/issue-12", "main", sha: "sha1", labels: "ai");
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(), Markers.Review);
        await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha1"), _kit.Run, default);

        _kit.Git.Refs["sha1"] = "sha1";
        _kit.Git.CommitsAhead["sha1"] = commitsOfSha1NotInSha2;
        _kit.Git.ChangedFiles = ["src/a.cs", "src/b.cs"];
        _kit.Git.ChangedFilesSince["sha1"] = ["src/b.cs", "src/from-main.cs"];
        _kit.Git.DiffText =
            "diff --git a/src/b.cs b/src/b.cs\n+new line in the PR\n" +
            "diff --git a/src/from-main.cs b/src/from-main.cs\n+merged from main\n";
        _kit.GitBucket.AddPull(30, "ai/issue-12", "main", sha: "sha2", labels: "ai");
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(previous: PreviousRows), Markers.Review);

        return await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha2"), _kit.Run, default);
    }

    [Fact]
    public async Task A_later_review_reads_only_the_new_commits_and_checks_the_previous_findings()
    {
        var outcome = await ReviewTwiceAsync();

        Assert.Equal(("sha1", "sha2"), _kit.Git.Diffs.Last());
        var changedFiles = File.ReadAllLines(Path.Combine(_kit.Workspace.Dir, "changed-files.txt"));
        Assert.Equal(["src/b.cs"], changedFiles);   // src/from-main.cs is not part of the PR
        var diff = File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "diff.patch"));
        Assert.Contains("new line in the PR", diff);
        Assert.DoesNotContain("merged from main", diff);
        var context = File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "context.md"));
        Assert.Contains("Incremental review (automatic review 2 of 3)", context);
        Assert.Contains("## Previous AI review", context);
        Assert.Contains("Null check missing", context);
        Assert.Contains("incremental, review 2 of 3", outcome.Detail);
        Assert.Contains("1 still open", outcome.Detail);

        var comment = _kit.GitBucket.CommentsOf(30).Single();
        Assert.Contains("<!-- ai-runner:review sha=sha2 n=2 -->", comment.Body);
        var state = _kit.State.GetReview(JobTestKit.Repo, 30)!;
        Assert.Equal(("sha2", 2), (state.ReviewedSha, state.ReviewCount));
    }

    [Fact]
    public async Task A_review_after_history_was_rewritten_reads_the_whole_pr()
    {
        await ReviewTwiceAsync(commitsOfSha1NotInSha2: 2);

        Assert.Equal(("origin/main", "sha2"), _kit.Git.Diffs.Last());
        var context = File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "context.md"));
        Assert.DoesNotContain("## Previous AI review", context);
    }

    [Fact]
    public async Task The_review_that_uses_the_last_automatic_one_says_so()
    {
        _kit.GitBucket.AddPull(30, "ai/issue-12", "main", sha: "sha3", labels: "ai");
        var earlier = new ReviewState("sha2", null, "needs changes") { ReviewedSha = "sha2", ReviewCount = 2 };
        _kit.State.SetReview(JobTestKit.Repo, 30, earlier);
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(), Markers.Review);

        await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha3"), _kit.Run, default);

        var comment = _kit.GitBucket.CommentsOf(30).Single();
        Assert.Contains("The 3 automatic reviews of limits.max_reviews are used", comment.Body);
        Assert.Contains(Labels.ReviewFull, comment.Body);
        Assert.Equal(3, _kit.State.GetReview(JobTestKit.Repo, 30)!.ReviewCount);
    }

    [Fact]
    public async Task The_label_asks_for_one_full_review_past_the_limit_and_is_removed()
    {
        _kit.GitBucket.AddPull(30, "ai/issue-12", "main", sha: "sha4", labels: ["ai", Labels.ReviewFull]);
        var atLimit = new ReviewState("sha3", null, "needs changes") { ReviewedSha = "sha3", ReviewCount = 3 };
        _kit.State.SetReview(JobTestKit.Repo, 30, atLimit);
        _kit.Git.Refs["sha3"] = "sha3";
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(), Markers.Review);

        var outcome = await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha4"), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Contains((30, Labels.ReviewFull), _kit.GitBucket.RemovedLabels);
        Assert.Equal(("origin/main", "sha4"), _kit.Git.Diffs.Last());   // full, although sha3 is an ancestor
        var context = File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "context.md"));
        Assert.Contains($"Full review asked for with the label `{Labels.ReviewFull}`", context);
        var state = _kit.State.GetReview(JobTestKit.Repo, 30)!;
        Assert.Equal((3, "sha4"), (state.ReviewCount, state.FullReviewSha));
    }

    [Fact]
    public async Task Without_local_state_one_more_review_carries_the_count_on_from_the_comment_tag()
    {
        _kit.GitBucket.AddPull(30, "ai/issue-12", "main", sha: "sha4", labels: "ai");
        var tag = new ReviewHistory("sha3", 3).Tag();
        _kit.GitBucket.AddBotComment(30, Markers.Wrap(Samples.Review(), Markers.Review) + "\n" + tag);
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(), Markers.Review);

        await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha4"), _kit.Run, default);
        _kit.GitBucket.AddPull(30, "ai/issue-12", "main", sha: "sha5", labels: "ai");
        var next = await _kit.Loader.LoadPullAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha5"), default);

        Assert.Equal(4, _kit.State.GetReview(JobTestKit.Repo, 30)!.ReviewCount);
        Assert.Contains("<!-- ai-runner:review sha=sha4 n=4 -->", _kit.GitBucket.CommentsOf(30).Single().Body);
        Assert.Null(next.Decision.Job);   // the stored count stops the next head again
    }

    [Fact]
    public async Task A_sub_issue_pr_is_reviewed_with_the_parent_spec_and_the_others_at_the_feature_branch()
    {
        _kit.GitBucket.AddIssue(12, "Export", null, "ai", Labels.Parent);
        var parentSpec = Samples.SpecWith([Samples.DefaultComponent], Samples.ApiTable, Samples.UiTable);
        _kit.GitBucket.AddBotComment(12, Markers.Wrap(parentSpec, Markers.Spec));
        var task = new SubTask(1, Samples.DefaultComponent, "Task", "Do the work.", "src/x.cs", [], false);
        var body = SubIssueBody.Build(new SubIssueDraft(12, task, 2, "contract"));
        _kit.GitBucket.AddIssue(21, "#12.1 Task", body, "ai", Labels.SubIssue, Labels.Ready, Labels.Simple);
        _kit.GitBucket.AddPull(
            30, "ai/issue-12-1", "feat/issue-12", sha: "sha1", body: "Issue: james/demo#21", labels: "ai");
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(), Markers.Review);

        await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha1"), _kit.Run, default);

        var contextFile = Path.Combine(_kit.Workspace.Dir, "context.md");
        var context = File.ReadAllText(contextFile);
        Assert.Contains("## Linked issue: SPEC (from parent #12)", context);
        Assert.Contains("PaginationResponse<ExportRow>", context);
        Assert.Equal("feat/issue-12", _kit.Workspace.Requests.Single().OthersAt);
    }

    [Fact]
    public async Task The_final_pr_of_a_parent_issue_is_reviewed_with_the_others_at_the_feature_branch()
    {
        _kit.GitBucket.AddPull(30, "feat/issue-12", "main", sha: "sha1", labels: "ai");
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(), Markers.Review);

        await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha1"), _kit.Run, default);

        Assert.Equal("feat/issue-12", _kit.Workspace.Requests.Single().OthersAt);
    }

    [Fact]
    public async Task Docs_only_change_is_skipped_without_copilot()
    {
        _kit.GitBucket.AddPull(30, "docs", "main", sha: "sha1", labels: "ai");
        _kit.Git.ChangedFiles = ["README.md", "docs/guide.md"];

        var reviewRequest = _kit.Request(JobType.ReviewPullRequest, 30, "sha1");
        var outcome = await Job.RunAsync(reviewRequest, _kit.Run, default);

        Assert.Equal("skipped", outcome.Result);
        Assert.Empty(_kit.Copilot.Requests);
        Assert.Equal("skipped", _kit.State.GetReview(JobTestKit.Repo, 30)!.Result);
    }

    [Fact]
    public async Task Bad_verdict_fails_without_posting()
    {
        _kit.GitBucket.AddPull(30, "feature", "main", sha: "sha1", labels: "ai");
        var badReview = Samples.Review(verdict: "great");
        _kit.Copilot.StdOut = Markers.Wrap(badReview, Markers.Review);

        await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha1"), _kit.Run, default));

        Assert.Empty(_kit.GitBucket.CommentsOf(30));
    }
}

public sealed class SequenceSubIssuesJobTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private SequenceSubIssuesJob Job => new(_kit.GitBucket, _kit.Loader);

    public void Dispose() => _kit.Dispose();

    private void Arrange()
    {
        _kit.GitBucket.AddIssue(12, "Export", null, "ai", Labels.Parent, Labels.Ready, Labels.Complex);
        _kit.GitBucket.AddIssue(
            20, "#12.1 Entity", "Parent: #12\nOrder: 1 of 2", "ai",
            Labels.SubIssue, Labels.Ready, Labels.Simple, Labels.Implementing);
        _kit.GitBucket.AddIssue(
            21, "#12.2 Command", "Parent: #12\nOrder: 2 of 2", "ai", Labels.SubIssue, Labels.Ready, Labels.Simple);
        var subIssueList = SubIssueBody.ListMarker([20, 21]);
        _kit.GitBucket.AddBotComment(12, subIssueList);
    }

    [Fact]
    public async Task Waits_until_the_first_sub_issue_is_merged()
    {
        Arrange();

        var sequenceRequest = _kit.Request(JobType.SequenceSubIssues, 12);
        var outcome = await Job.RunAsync(sequenceRequest, _kit.Run, default);

        Assert.True(outcome.Quiet);
        Assert.DoesNotContain(Labels.Implement, _kit.GitBucket.LabelsOf(21));
    }

    [Fact]
    public async Task Merged_sub_issue_starts_the_next_one()
    {
        Arrange();
        _kit.GitBucket.AddPull(50, "ai/issue-12-1", "feat/issue-12", merged: true, state: "closed");

        var sequenceRequest = _kit.Request(JobType.SequenceSubIssues, 12);
        var outcome = await Job.RunAsync(sequenceRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(0, outcome.CopilotCalls);
        Assert.Contains(Labels.Implement, _kit.GitBucket.LabelsOf(21));
        Assert.Single(_kit.GitBucket.CommentsOf(21));

        // next poll: already started, nothing more
        var againRequest = _kit.Request(JobType.SequenceSubIssues, 12);
        var again = await Job.RunAsync(againRequest, _kit.Run, default);
        Assert.True(again.Quiet);
    }

    [Fact]
    public async Task All_merged_opens_one_final_pr()
    {
        Arrange();
        _kit.GitBucket.AddPull(
            50, "ai/issue-12-1", "feat/issue-12", merged: true, state: "closed", title: "#20: Entity (Copilot)");
        _kit.GitBucket.AddPull(
            51, "ai/issue-12-2", "feat/issue-12", merged: true, state: "closed", title: "#21: Command (Copilot)");

        var sequenceRequest = _kit.Request(JobType.SequenceSubIssues, 12);
        await Job.RunAsync(sequenceRequest, _kit.Run, default);

        var final = _kit.GitBucket.Pulls.Single(pull => pull.Head.Ref == "feat/issue-12");
        Assert.Equal("main", final.Base.Ref);
        Assert.Contains("#50", final.Body);
        Assert.Contains("#51", final.Body);
        Assert.Contains("Issue: james/demo#12", final.Body);
        Assert.DoesNotContain("Closes", final.Body);
        Assert.Contains(SubIssueBody.FinalPrsMarker, _kit.GitBucket.CommentsOf(12).Last().Body);

        var againRequest = _kit.Request(JobType.SequenceSubIssues, 12);
        var again = await Job.RunAsync(againRequest, _kit.Run, default);
        Assert.True(again.Quiet);
        Assert.Single(_kit.GitBucket.Pulls, pull => pull.Head.Ref == "feat/issue-12");
    }
}
