namespace AiRunner.Tests.Core;

public class JobPlannerTests
{
    private static readonly RepoConfig Repo = new() { Owner = "o", Name = "r", Workflows = [.. Workflows.All] };
    private long _id = 100;

    private static GbIssue Issue(params string[] labels) => Samples.Issue(7, labels);

    private GbComment Human(string body) => Samples.Human(++_id, body);

    private GbComment Analysis() => Samples.Bot(++_id, Markers.Wrap("x", Markers.Analysis));

    private static JobType? Type(PlanDecision d) => d.Job?.Type;

    [Fact]
    public void New_issue_gets_round_1() =>
        Assert.Equal(JobType.AnalyzeIssue, Type(JobPlanner.PlanIssue(Repo, Issue("ai"), [], 0)));

    [Fact]
    public void Without_comments_the_analysis_decision_asks_for_them()
    {
        var d = JobPlanner.PlanIssue(Repo, Issue("ai"), null, 0);
        Assert.Null(d.Job);
        Assert.True(d.NeedsComments);
    }

    [Fact]
    public void Label_decisions_do_not_need_comments()
    {
        var d = JobPlanner.PlanIssue(Repo, Issue("ai", Labels.Implement), null, 0);
        Assert.Equal(JobType.ImplementIssue, Type(d));
        Assert.False(d.NeedsComments);
        Assert.Contains("will be refused", d.Reason);   // not ai:ready + ai:simple
        Assert.True(JobPlanner.PlanIssue(Repo, Issue("ai", Labels.Parent), null, 0).Routine);
    }

    [Fact]
    public void Waits_for_at_ai_after_an_analysis()
    {
        var comments = new List<GbComment> { Analysis(), Human("thanks, Q1: CSV only") };
        Assert.Null(JobPlanner.PlanIssue(Repo, Issue("ai"), comments, 0).Job);

        comments.Add(Human("Q1: CSV only @ai"));
        var d = JobPlanner.PlanIssue(Repo, Issue("ai"), comments, 0);
        Assert.Equal(JobType.AnalyzeIssue, Type(d));
        Assert.Contains("round 2", d.Reason);
    }

    [Fact]
    public void Email_like_text_is_not_a_trigger() =>
        Assert.Null(JobPlanner.PlanIssue(Repo, Issue("ai"), [Analysis(), Human("mail me at x@ai.com")], 0).Job);

    [Fact]
    public void Comment_written_during_the_run_still_triggers()
    {
        // Comment 101 was written while Copilot ran on a context that ended at 100; the analysis is 102.
        var during = Samples.Human(101, "A1 wrong @ai");
        var analysis = Samples.Bot(102, Markers.Wrap("x", Markers.Analysis) + BotComments.ContextUptoTag(100));
        Assert.Equal(JobType.AnalyzeIssue, Type(JobPlanner.PlanIssue(Repo, Issue("ai"), [during, analysis], 0)));
    }

    [Fact]
    public void Already_handled_comment_does_not_trigger()
    {
        var answer = Human("Q1: yes @ai");
        Assert.Null(JobPlanner.PlanIssue(Repo, Issue("ai"), [Analysis(), answer], answer.Id).Job);
    }

    [Fact]
    public void Round_limit_still_plans_the_job_so_it_can_stop_with_a_message()
    {
        var comments = Enumerable.Range(0, 5).Select(_ => Analysis()).ToList();
        comments.Add(Human("@ai"));
        var d = JobPlanner.PlanIssue(Repo, Issue("ai"), comments, 0);
        Assert.Equal(JobType.AnalyzeIssue, Type(d));
        Assert.Contains("limit", d.Reason);
    }

    [Theory]
    [InlineData(new[] { "ai", Labels.Failed, Labels.Implement }, null)]
    [InlineData(new[] { "ai", Labels.NeedsCloudAgent }, null)]
    [InlineData(new[] { "ai", Labels.Implement }, JobType.ImplementIssue)]
    [InlineData(new[] { "ai", Labels.Implement, Labels.Implementing }, null)]
    [InlineData(new[] { "ai", Labels.Breakdown }, JobType.BreakdownIssue)]
    [InlineData(new[] { "ai", Labels.Breakdown, Labels.Parent }, null)]
    [InlineData(new[] { "ai", Labels.Parent }, JobType.SequenceSubIssues)]
    public void Labels_decide_the_job(string[] labels, JobType? expected) =>
        Assert.Equal(expected, Type(JobPlanner.PlanIssue(Repo, Issue(labels), [Analysis()], 0)));

    [Fact]
    public void Sub_issue_is_analyzed_only_on_request()
    {
        Assert.Null(JobPlanner.PlanIssue(Repo, Issue("ai", Labels.SubIssue), [], 0).Job);
        Assert.Equal(JobType.AnalyzeIssue,
            Type(JobPlanner.PlanIssue(Repo, Issue("ai", Labels.SubIssue), [Human("too big, split it @ai")], 0)));
    }

    [Fact]
    public void Disabled_workflow_is_skipped()
    {
        var repo = new RepoConfig { Owner = "o", Name = "r", Workflows = [Workflows.Analyze] };
        Assert.Null(JobPlanner.PlanIssue(repo, Issue("ai", Labels.Implement), [], 0).Job);
    }

    // ---- pull requests ----

    private static GbPull Pull(string head = "feature/x", string title = "Add export", bool? draft = null, string user = "alice",
        params string[] labels) =>
        new(3, title, new GbRef(head, "sha1"), new GbRef("main", "sha0"), DateTimeOffset.UtcNow, User: new GbUser(user), Draft: draft,
            Labels: labels.Select(l => new GbLabel(l)).ToList());

    private static readonly RepoConfig AllPrs =
        new() { Owner = "o", Name = "r", Workflows = [Workflows.Review], Review = new ReviewConfig { Scope = "all" } };

    [Fact]
    public void New_pr_and_new_head_are_reviewed_once()
    {
        Assert.Equal("sha1", JobPlanner.PlanPull(AllPrs, Pull(), null, "bot", "ai").Job?.Payload);
        Assert.Null(JobPlanner.PlanPull(AllPrs, Pull(), "sha1", "bot", "ai").Job);
        Assert.NotNull(JobPlanner.PlanPull(AllPrs, Pull(), "sha0", "bot", "ai").Job);
    }

    [Fact]
    public void Draft_and_skip_ai_are_skipped()
    {
        Assert.Null(JobPlanner.PlanPull(AllPrs, Pull(draft: true), null, "bot", "ai").Job);
        Assert.Null(JobPlanner.PlanPull(AllPrs, Pull(title: "[skip-ai] bump"), null, "bot", "ai").Job);
    }

    [Fact]
    public void By_default_only_labelled_prs_and_the_runners_own_ai_prs_are_reviewed()
    {
        Assert.True(Repo.Review.IsScoped);
        Assert.Null(JobPlanner.PlanPull(Repo, Pull(), null, "bot", "ai").Job);
        Assert.NotNull(JobPlanner.PlanPull(Repo, Pull(labels: "ai"), null, "bot", "ai").Job);
        Assert.NotNull(JobPlanner.PlanPull(Repo, Pull(head: "ai/issue-7", user: "bot"), null, "bot", "ai").Job);
        Assert.NotNull(JobPlanner.PlanPull(Repo, Pull(head: "feat/issue-12", user: "bot"), null, "bot", "ai").Job);

        // Not enough on its own: an ai/* branch pushed by someone else (or another runner), or any other PR of the runner's user.
        Assert.Null(JobPlanner.PlanPull(Repo, Pull(head: "ai/issue-7", user: "alice"), null, "bot", "ai").Job);
        Assert.Null(JobPlanner.PlanPull(Repo, Pull(user: "bot"), null, "bot", "ai").Job);
    }
}

public class ContextBuilderTests
{
    [Fact]
    public void Issue_context_lists_comments_oldest_first_and_marks_the_bot()
    {
        var issue = new GbIssue(7, "Export", "Please export CSV", new GbUser("alice"), [], DateTimeOffset.UtcNow, null);
        var t = DateTimeOffset.UtcNow;
        var comments = new[]
        {
            new GbComment(3, "Q1: CSV @ai", new GbUser("alice"), t.AddMinutes(2)),
            new GbComment(2, BotComments.Marker + "\nQ1: which format?", new GbUser("hai"), t.AddMinutes(1)),
        };

        var text = ContextBuilder.ForIssue(issue, comments, round: 2);

        Assert.StartsWith("# Issue #7: Export", text);
        Assert.Contains("Analysis round: 2", text);
        Assert.Contains("## hai [bot]\nQ1: which format?", text.Replace("\r\n", "\n"));
        Assert.DoesNotContain(BotComments.Marker, text);
        Assert.True(text.IndexOf("## hai [bot]", StringComparison.Ordinal) < text.IndexOf("## alice", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Closes #12", 12)]
    [InlineData("Like #4, this adds export.\n\nFixes #9", 9)]
    [InlineData("Fixes issue #3 and #4", 3)]
    [InlineData("see http://x/y#5", null)]
    [InlineData(null, null)]
    public void Linked_issue_from_pr_body(string? body, int? expected) => Assert.Equal(expected, ContextBuilder.LinkedIssue(body));
}

public class RunnerConfigTests
{
    [Fact]
    public void Reads_repos_yml_with_defaults()
    {
        var config = RunnerConfig.Parse("""
            gitBucket:
              baseUrl: http://gitbucket
            repos:
              - owner: o
                name: r
                workflows: [analyze, implement]
                models: { analyze: big-model }
                commands:
                  build: npm run build
                  test: npm test
                review: { scope: scoped, skipPaths: ["**/*.md"] }
            """);
        var repo = config.Repos.Single();

        Assert.Equal("o/r", repo.FullName);
        Assert.True(repo.IsEnabled("analyze"));
        Assert.False(repo.IsEnabled("review"));
        Assert.Equal("big-model", repo.ModelFor("analyze"));
        Assert.Null(repo.ModelFor("implement"));
        Assert.Equal(["npm run build", "npm test"], repo.Commands.Verification());
        Assert.True(repo.Review.IsScoped);
        Assert.Equal(15, repo.Limits.MaxChangedFiles);
        Assert.Equal(90, config.PollIntervalSeconds);
    }
}
