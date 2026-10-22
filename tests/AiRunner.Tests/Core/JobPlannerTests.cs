using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

public class JobPlannerTests
{
    private static readonly ProjectConfig Project = TestProjects.Single();
    private long _id = 100;

    private static HostIssue Issue(params string[] labels) => Samples.Issue(7, labels);

    private HostComment Human(string body) => Samples.Human(++_id, body);

    private HostComment Analysis()
    {
        var analysisBody = Markers.Wrap("x", Markers.Analysis);
        return Samples.Bot(++_id, analysisBody);
    }

    private static JobType? Type(PlanDecision decision) => decision.Job?.Type;

    private static PlanDecision PlanIssue(
        HostIssue issue,
        IReadOnlyList<HostComment>? comments,
        long lastHandledCommentId = 0)
    {
        var input = new IssuePlanInput(Project, TestProjects.Repo, issue, comments, lastHandledCommentId);

        return JobPlanner.PlanIssue(input);
    }

    private static PlanDecision PlanPull(
        ProjectConfig project,
        HostPull pull,
        string? reviewedSha = null,
        IReadOnlyList<HostComment>? comments = null)
    {
        var input = new PullPlanInput(project, TestProjects.Repo, pull, reviewedSha, BotLogin: "bot", Comments: comments);

        return JobPlanner.PlanPull(input);
    }

    [Fact]
    public void New_issue_gets_round_1()
    {
        var issue = Issue("ai");
        var decision = PlanIssue(issue, []);

        Assert.Equal(JobType.AnalyzeIssue, Type(decision));
    }

    [Fact]
    public void Without_comments_the_analysis_decision_asks_for_them()
    {
        var issue = Issue("ai");
        var decision = PlanIssue(issue, null);

        Assert.Null(decision.Job);
        Assert.True(decision.NeedsComments);
    }

    [Fact]
    public void Label_decisions_do_not_need_comments()
    {
        var implementIssue = Issue("ai", Labels.Implement);
        var decision = PlanIssue(implementIssue, null);

        Assert.Equal(JobType.ImplementIssue, Type(decision));
        Assert.False(decision.NeedsComments);
        Assert.Contains("will be refused", decision.Reason);   // not ai:ready + ai:simple

        var parentIssue = Issue("ai", Labels.Parent);
        var parentDecision = PlanIssue(parentIssue, null);

        Assert.True(parentDecision.Routine);
    }

    [Fact]
    public void Waits_for_at_ai_after_an_analysis()
    {
        var analysis = Analysis();
        var question = Human("thanks, Q1: CSV only");
        var comments = new List<HostComment> { analysis, question };
        var issue = Issue("ai");
        var waitingDecision = PlanIssue(issue, comments);

        Assert.Null(waitingDecision.Job);

        var answer = Human("Q1: CSV only @ai");
        comments.Add(answer);
        var decision = PlanIssue(issue, comments);

        Assert.Equal(JobType.AnalyzeIssue, Type(decision));
        Assert.Contains("round 2", decision.Reason);
    }

    [Fact]
    public void Email_like_text_is_not_a_trigger()
    {
        var analysis = Analysis();
        var emailComment = Human("mail me at x@ai.com");
        var issue = Issue("ai");
        var decision = PlanIssue(issue, [analysis, emailComment]);

        Assert.Null(decision.Job);
    }

    [Fact]
    public void Comment_written_during_the_run_still_triggers()
    {
        // Comment 101 was written while Copilot ran on a context that ended at 100; the analysis is 102.
        var during = Samples.Human(101, "A1 wrong @ai");
        var analysisBody = AtAiThread.ReplyBody("x", Markers.Analysis, 100);
        var analysis = Samples.Bot(102, analysisBody);
        var issue = Issue("ai");
        var decision = PlanIssue(issue, [during, analysis]);

        Assert.Equal(JobType.AnalyzeIssue, Type(decision));
    }

    [Fact]
    public void Already_handled_comment_does_not_trigger()
    {
        var answer = Human("Q1: yes @ai");
        var analysis = Analysis();
        var issue = Issue("ai");
        var decision = PlanIssue(issue, [analysis, answer], answer.Id);

        Assert.Null(decision.Job);
    }

    [Fact]
    public void Round_limit_still_plans_the_job_so_it_can_stop_with_a_message()
    {
        var comments = Enumerable.Range(0, 5).Select(_ => Analysis()).ToList();
        var atAiComment = Human("@ai");
        comments.Add(atAiComment);
        var issue = Issue("ai");
        var decision = PlanIssue(issue, comments);

        Assert.Equal(JobType.AnalyzeIssue, Type(decision));
        Assert.Contains("limit", decision.Reason);
    }

    [Theory]
    [InlineData(new[] { "ai", Labels.Failed, Labels.Implement }, null)]
    [InlineData(new[] { "ai", Labels.NeedsCloudAgent }, null)]
    [InlineData(new[] { "ai", Labels.Implement }, JobType.ImplementIssue)]
    [InlineData(new[] { "ai", Labels.Implement, Labels.Implementing }, null)]
    [InlineData(new[] { "ai", Labels.Breakdown }, JobType.BreakdownIssue)]
    [InlineData(new[] { "ai", Labels.Breakdown, Labels.Parent }, null)]
    [InlineData(new[] { "ai", Labels.Parent }, JobType.SequenceSubIssues)]
    public void Labels_decide_the_job(string[] labels, JobType? expected)
    {
        var analysis = Analysis();
        var issue = Issue(labels);
        var decision = PlanIssue(issue, [analysis]);

        Assert.Equal(expected, Type(decision));
    }

    [Fact]
    public void Sub_issue_is_analyzed_only_on_request()
    {
        var subIssue = Issue("ai", Labels.SubIssue);
        var withoutRequest = PlanIssue(subIssue, []);

        Assert.Null(withoutRequest.Job);

        var request = Human("too big, split it @ai");
        var withRequest = PlanIssue(subIssue, [request]);

        Assert.Equal(JobType.AnalyzeIssue, Type(withRequest));
    }

    [Fact]
    public void Disabled_workflow_is_skipped()
    {
        var repo = TestProjects.Single([Workflows.Analyze]);
        var issue = Issue("ai", Labels.Implement);
        var input = new IssuePlanInput(repo, TestProjects.Repo, issue, Comments: [], LastHandledCommentId: 0);
        var decision = JobPlanner.PlanIssue(input);

        Assert.Null(decision.Job);
    }

    // ---- pull requests ----

    private static HostPull Pull(
        string head = "feature/x",
        string title = "Add export",
        bool? draft = null,
        string user = "alice",
        params string[] labels) =>
        new(
            3,
            title,
            new HostRef(head, "sha1"),
            new HostRef("main", "sha0"),
            DateTimeOffset.UtcNow,
            User: new HostUser(user),
            Draft: draft,
            Labels: labels.Select(label => new HostLabel(label)).ToList());

    private static readonly ProjectConfig AllPrs = TestProjects.Single([Workflows.Review], reviewScope: "all");

    [Fact]
    public void New_pr_and_new_head_are_reviewed_once()
    {
        var pull = Pull();
        var newPull = PlanPull(AllPrs, pull);
        var reviewedHead = PlanPull(AllPrs, pull, reviewedSha: "sha1");
        var movedHead = PlanPull(AllPrs, pull, reviewedSha: "sha0");

        Assert.Equal("sha1", newPull.Job?.Payload);
        Assert.Null(reviewedHead.Job);
        Assert.NotNull(movedHead.Job);
    }

    [Fact]
    public void Reviews_stop_at_max_reviews_and_the_label_asks_for_one_more_full_review()
    {
        var pull = Pull();
        var labelled = Pull(labels: Labels.ReviewFull);
        var atLimit = new PullPlanInput(AllPrs, TestProjects.Repo, pull, "sha0", BotLogin: "bot")
        {
            ReviewCount = AllPrs.Settings.Limits.MaxReviews,
        };
        var labelledAtLimit = atLimit with { Pull = labelled };
        var labelledAndDone = labelledAtLimit with { FullReviewSha = "sha1" };

        var limited = JobPlanner.PlanPull(atLimit);
        var asked = JobPlanner.PlanPull(labelledAtLimit);
        var askedSameHead = JobPlanner.PlanPull(labelledAtLimit with { ReviewedSha = "sha1" });
        var done = JobPlanner.PlanPull(labelledAndDone);

        Assert.Null(limited.Job);
        Assert.Contains("review limit 3 reached (limits.max_reviews)", limited.Reason);
        Assert.Equal(JobType.ReviewPullRequest, asked.Job?.Type);
        Assert.Contains(Labels.ReviewFull, asked.Reason);
        Assert.NotNull(askedSameHead.Job);
        Assert.Null(done.Job);
    }

    [Fact]
    public void Draft_and_skip_ai_are_skipped()
    {
        var draftPull = Pull(draft: true);
        var skipPull = Pull(title: "[skip-ai] bump");
        var draftDecision = PlanPull(AllPrs, draftPull);
        var skipDecision = PlanPull(AllPrs, skipPull);

        Assert.Null(draftDecision.Job);
        Assert.Null(skipDecision.Job);
    }

    [Fact]
    public void By_default_only_labelled_prs_and_the_runners_own_ai_prs_are_reviewed()
    {
        Assert.True(Project.Settings.Review.IsScoped);

        var plainPull = Pull();
        var labelledPull = Pull(labels: "ai");
        var runnerAiPull = Pull(head: "ai/issue-7", user: "bot");
        var runnerFeaturePull = Pull(head: "feat/issue-12", user: "bot");

        Assert.Null(PlanPull(Project, plainPull).Job);
        Assert.NotNull(PlanPull(Project, labelledPull).Job);
        Assert.NotNull(PlanPull(Project, runnerAiPull).Job);
        Assert.NotNull(PlanPull(Project, runnerFeaturePull).Job);

        // Not enough on its own: an ai/* branch pushed by someone else (or another runner),
        // or any other PR of the runner's user.
        var foreignAiPull = Pull(head: "ai/issue-7", user: "alice");
        var runnerOtherPull = Pull(user: "bot");

        Assert.Null(PlanPull(Project, foreignAiPull).Job);
        Assert.Null(PlanPull(Project, runnerOtherPull).Job);
    }

    // ---- review fixes ----

    private HostComment FixReply(long upto)
    {
        var replyBody = Samples.FixReply(upto);
        return Samples.Bot(++_id, replyBody);
    }

    [Fact]
    public void At_ai_on_a_pr_in_scope_asks_for_a_review_fix_before_the_review()
    {
        var looksOdd = Human("looks odd");
        var atAiComment = Human("@ai 1: fix, 2: skip");
        var pull = Pull(labels: "ai");
        var decision = PlanPull(Project, pull, comments: [looksOdd, atAiComment]);

        Assert.Equal(JobType.FixReview, Type(decision));
        Assert.Equal(atAiComment.Id.ToString(), decision.Job!.Payload);
    }

    [Fact]
    public void Without_comments_a_pr_in_fix_scope_asks_for_them_and_keeps_the_review_decision()
    {
        var labelledPull = Pull(labels: "ai");
        var decision = PlanPull(Project, labelledPull);

        Assert.True(decision.NeedsComments);
        Assert.Equal(JobType.ReviewPullRequest, Type(decision));

        var plainPull = Pull();
        var outOfScope = PlanPull(Project, plainPull);

        Assert.False(outOfScope.NeedsComments);

        var reviewOnly = TestProjects.Single([Workflows.Review]);
        var reviewOnlyDecision = PlanPull(reviewOnly, labelledPull);

        Assert.False(reviewOnlyDecision.NeedsComments);
    }

    [Fact]
    public void Answered_or_bot_at_ai_comments_do_not_ask_again()
    {
        var atAiComment = Human("@ai fix 1");
        var labelledPull = Pull(labels: "ai");

        var answeredReply = FixReply(atAiComment.Id);
        var answered = PlanPull(Project, labelledPull, comments: [atAiComment, answeredReply]);

        Assert.Equal(JobType.ReviewPullRequest, Type(answered));

        var handledInput = new PullPlanInput(
            Project,
            TestProjects.Repo,
            labelledPull,
            ReviewedSha: null,
            BotLogin: "bot",
            Comments: [atAiComment],
            LastHandledCommentId: atAiComment.Id);
        var handled = JobPlanner.PlanPull(handledInput);

        Assert.Equal(JobType.ReviewPullRequest, Type(handled));

        var botComment = Samples.Bot(++_id, "@ai said");
        var botOnly = PlanPull(Project, labelledPull, comments: [botComment]);

        Assert.Equal(JobType.ReviewPullRequest, Type(botOnly));

        // A comment written while the fix ran is newer than what the reply read: still a request.
        var during = Human("@ai also rename it");
        var newerReply = FixReply(atAiComment.Id);
        var request = PlanPull(Project, labelledPull, comments: [atAiComment, during, newerReply]);

        Assert.Equal(JobType.FixReview, Type(request));
    }

    [Fact]
    public void Review_fix_needs_the_fix_workflow_and_a_pr_in_scope()
    {
        var reviewOnly = TestProjects.Single([Workflows.Review], reviewScope: "all");
        var labelledPull = Pull(labels: "ai");
        var fixComment = Human("@ai fix");
        var reviewOnlyDecision = PlanPull(reviewOnly, labelledPull, comments: [fixComment]);

        Assert.Equal(JobType.ReviewPullRequest, Type(reviewOnlyDecision));

        // review.scope: all reviews every PR, but a fix still only pushes to PRs labelled ai or the runner's own.
        var all = TestProjects.Single(reviewScope: "all");
        var plainPull = Pull();
        var plainDecision = PlanPull(all, plainPull, comments: [fixComment]);

        Assert.Equal(JobType.ReviewPullRequest, Type(plainDecision));

        var runnerPull = Pull(head: "ai/issue-7", user: "bot");
        var runnerDecision = PlanPull(all, runnerPull, comments: [fixComment]);

        Assert.Equal(JobType.FixReview, Type(runnerDecision));

        var fixOnly = TestProjects.Single([Workflows.Fix]);
        var fixOnlyDecision = PlanPull(fixOnly, labelledPull, comments: [fixComment]);

        Assert.Equal(JobType.FixReview, Type(fixOnlyDecision));

        var noComments = PlanPull(fixOnly, labelledPull, comments: []);

        Assert.Null(noComments.Job);
    }

    [Fact]
    public void Closed_or_merged_pr_gets_neither_fix_nor_review()
    {
        var labelledPull = Pull(labels: "ai");
        var closed = labelledPull with
        {
            State = "closed"
        };

        var merged = labelledPull with
        {
            MergedAt = DateTimeOffset.UtcNow
        };
        var fixComment = Human("@ai fix");
        var closedDecision = PlanPull(Project, closed, comments: [fixComment]);
        var mergedDecision = PlanPull(Project, merged, comments: [fixComment]);

        Assert.Null(closedDecision.Job);
        Assert.Null(mergedDecision.Job);
    }

    [Fact]
    public void A_review_fix_needs_a_command_to_check_it_with()
    {
        var pull = Pull(head: "ai/issue-7", labels: "ai");
        var noCommands = TestProjects.Single([Workflows.Fix]);
        var withBuild = TestProjects.Single([Workflows.Fix], commands: new CommandsConfig { Build = "make build" });

        var noCommandsCheck = new FixCheckInput(
            noCommands, noCommands.Components[0], pull, [], "main", pull.Head.Sha);
        var noCommandsReason = JobPlanner.WhyNotFix(noCommandsCheck);

        Assert.Contains("No build, test or lint command", noCommandsReason);

        var withBuildCheck = new FixCheckInput(
            withBuild, withBuild.Components[0], pull, [], "main", pull.Head.Sha);
        var withBuildReason = JobPlanner.WhyNotFix(withBuildCheck);

        Assert.Null(withBuildReason);
    }

    [Fact]
    public void Review_fix_limit_still_runs_the_job_so_it_can_answer()
    {
        var repo = TestProjects.Single([Workflows.Fix], maxReviewFixes: 1);
        var first = Human("@ai fix 1");
        var firstReply = FixReply(first.Id);
        var again = Human("@ai again");
        var pull = Pull(labels: "ai");
        var decision = PlanPull(repo, pull, comments: [first, firstReply, again]);

        Assert.Equal(JobType.FixReview, Type(decision));
        Assert.Contains("limit 1 reached", decision.Reason);
    }
}

public class ContextBuilderTests
{
    [Fact]
    public void Issue_context_lists_comments_oldest_first_and_marks_the_bot()
    {
        var issue = new HostIssue(
            7,
            "Export",
            "Please export CSV",
            new HostUser("alice"),
            [],
            DateTimeOffset.UtcNow,
            null);
        var createdAt = DateTimeOffset.UtcNow;
        var humanCreatedAt = createdAt.AddMinutes(2);
        var botCreatedAt = createdAt.AddMinutes(1);
        var comments = new[]
        {
            new HostComment(3, "Q1: CSV @ai", new HostUser("alice"), humanCreatedAt),
            new HostComment(2, BotComments.Marker + "\nQ1: which format?", new HostUser("hai"), botCreatedAt),
        };

        var text = ContextBuilder.ForIssue(issue, comments, round: 2);

        Assert.StartsWith("# Issue #7: Export", text);
        Assert.Contains("Analysis round: 2", text);
        Assert.Contains("## hai [bot]\nQ1: which format?", text.Replace("\r\n", "\n"));
        Assert.DoesNotContain(BotComments.Marker, text);
        Assert.True(
            text.IndexOf("## hai [bot]", StringComparison.Ordinal) <
            text.IndexOf("## alice", StringComparison.Ordinal));
    }

    [Fact]
    public void Issue_context_keeps_only_the_newest_analysis_and_spec_in_full()
    {
        var issue = new HostIssue(
            7,
            "Export",
            "Please export CSV",
            new HostUser("alice"),
            [],
            DateTimeOffset.UtcNow,
            null);
        var at = DateTimeOffset.UtcNow;

        HostComment Block(int id, string text, string marker) =>
            new(id, BotComments.Marker + "\n" + Markers.Wrap(text, marker), new HostUser("hai"), at.AddMinutes(id));

        var comments = new[]
        {
            Block(1, "round one text", Markers.Analysis),
            Block(2, "old spec text", Markers.Spec),
            new HostComment(3, "Q1: CSV @ai", new HostUser("alice"), at.AddMinutes(3)),
            Block(4, "round two text", Markers.Analysis),
            Block(5, "new spec text", Markers.Spec),
            new HostComment(6, BotComments.Marker + "\nCopilot started", new HostUser("hai"), at.AddMinutes(6)),
        };

        var text = ContextBuilder.ForIssue(issue, comments, round: 3);

        Assert.DoesNotContain("round one text", text);
        Assert.DoesNotContain("old spec text", text);
        Assert.Contains("(AI analysis round 1: replaced by the newest analysis further down)", text);
        Assert.Contains("(SPEC: replaced by a newer SPEC further down)", text);
        Assert.Contains("round two text", text);
        Assert.Contains("new spec text", text);
        Assert.Contains("Q1: CSV @ai", text);
        Assert.Contains("Copilot started", text);
    }

    [Theory]
    [InlineData("Closes #12", 12)]
    [InlineData("Like #4, this adds export.\n\nFixes #9", 9)]
    [InlineData("Fixes issue #3 and #4", 3)]
    [InlineData("see http://x/y#5", null)]
    [InlineData(null, null)]
    public void Linked_issue_from_pr_body(string? body, int? expected) =>
        Assert.Equal(expected, ContextBuilder.LinkedIssue(body));
}
