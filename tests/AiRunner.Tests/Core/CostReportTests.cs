using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Tests.Core;

/// <summary>The cost totals of GET /api/cost: one table test per grouping and the parent + sub-issue sum.</summary>
public sealed class CostReportTests
{
    private static readonly DateTimeOffset Seen = DateTimeOffset.Parse("2026-03-05T12:00:00Z");

    private static readonly RunnerConfig Config = new()
    {
        Projects =
        [
            new ProjectConfig
            {
                Name = "shop",
                IssuesIn = "o/shop",
                Components = [new ComponentConfig { Name = "web", Repo = "o/shop" }],
            },
            new ProjectConfig
            {
                Name = "docs",
                IssuesIn = "o/docs",
                Components = [new ComponentConfig { Name = "site", Repo = "o/docs" }],
            },
        ],
    };

    private static CostRecord Cost(string at, string repo, int number, string workflow, double seconds, int? calls) =>
        new(DateTimeOffset.Parse(at), repo, number, workflow, seconds, calls);

    private static IssueSnapshot Issue(int number, int? parent = null) =>
        new("o/shop", number, "shop", $"Issue {number}", "open", ["ai"], [], Seen, Seen) { Parent = parent };

    private static PullSnapshot Pull(int number, string linkedIssue) =>
        new("o/shop", number, "shop", "PR", "open", false, "ai/issue-12", "main", "sha", Seen, Seen)
        {
            LinkedIssue = linkedIssue,
        };

    private static CostResult Build(
        CostGrouping by,
        IReadOnlyList<CostRecord> records,
        IReadOnlyList<IssueSnapshot>? issues = null,
        IReadOnlyList<PullSnapshot>? pulls = null,
        CostRange? range = null)
    {
        var input = new CostInput(records, issues ?? [], pulls ?? [], Config, TimeZoneInfo.Utc);

        return CostReport.Build(input, new CostQuery(by, range ?? new CostRange(null, null)));
    }

    [Fact]
    public void By_day_sums_calls_jobs_and_duration_per_local_day_oldest_first()
    {
        var records = new[]
        {
            Cost("2026-03-05T09:00:00Z", "o/shop", 1, "analyze", 60, 1),
            Cost("2026-03-04T23:30:00Z", "o/shop", 2, "implement", 600, 3),
            Cost("2026-03-05T18:00:00Z", "o/docs", 4, "review", 30, 1),
        };

        var result = Build(CostGrouping.Day, records);

        Assert.Equal(["2026-03-04", "2026-03-05"], result.Rows.Select(row => row.Key));
        var second = result.Rows[1];
        Assert.Equal((2, 2, 90), (second.Jobs, second.Calls, second.DurationSeconds));
        Assert.Equal((3, 5, 690), (result.Total.Jobs, result.Total.Calls, result.Total.DurationSeconds));
    }

    [Fact]
    public void A_day_is_the_day_in_the_given_time_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus7", TimeSpan.FromHours(7), "plus7", "plus7");
        var records = new[] { Cost("2026-03-04T20:00:00Z", "o/shop", 1, "analyze", 60, 1) };
        var input = new CostInput(records, [], [], Config, zone);

        var result = CostReport.Build(input, new CostQuery(CostGrouping.Day, new CostRange(null, null)));

        Assert.Equal("2026-03-05", Assert.Single(result.Rows).Key);
    }

    [Fact]
    public void From_and_to_are_inclusive_local_days()
    {
        var records = new[]
        {
            Cost("2026-03-03T10:00:00Z", "o/shop", 1, "analyze", 10, 1),
            Cost("2026-03-04T10:00:00Z", "o/shop", 1, "analyze", 10, 1),
            Cost("2026-03-05T23:59:00Z", "o/shop", 1, "analyze", 10, 1),
            Cost("2026-03-06T00:01:00Z", "o/shop", 1, "analyze", 10, 1),
        };
        var range = new CostRange(new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 5));

        var result = Build(CostGrouping.Day, records, range: range);

        Assert.Equal(["2026-03-04", "2026-03-05"], result.Rows.Select(row => row.Key));
    }

    [Fact]
    public void By_project_groups_by_the_project_of_the_repo_and_puts_unknown_repos_in_other()
    {
        var records = new[]
        {
            Cost("2026-03-05T09:00:00Z", "o/shop", 1, "analyze", 60, 1),
            Cost("2026-03-05T10:00:00Z", "o/shop", 2, "implement", 600, 4),
            Cost("2026-03-05T11:00:00Z", "o/docs", 3, "analyze", 30, 2),
            Cost("2026-03-05T12:00:00Z", "o/gone", 3, "analyze", 30, 1),
        };

        var result = Build(CostGrouping.Project, records);

        Assert.Equal(["shop", "docs", "(other)"], result.Rows.Select(row => row.Key));   // most calls first
        Assert.Equal((5, 2), (result.Rows[0].Calls, result.Rows[0].Jobs));
    }

    [Fact]
    public void By_issue_adds_the_sub_issues_to_their_parent()
    {
        var issues = new[] { Issue(12), Issue(21, parent: 12), Issue(22, parent: 12) };
        var records = new[]
        {
            Cost("2026-03-05T09:00:00Z", "o/shop", 12, "analyze", 60, 1),
            Cost("2026-03-05T10:00:00Z", "o/shop", 21, "implement", 600, 3),
            Cost("2026-03-05T11:00:00Z", "o/shop", 22, "implement", 300, 2),
            Cost("2026-03-05T12:00:00Z", "o/shop", 40, "analyze", 20, 1),
        };

        var result = Build(CostGrouping.Issue, records, issues);

        Assert.Equal(["o/shop#12", "o/shop#40"], result.Rows.Select(row => row.Key));
        var parent = result.Rows[0];
        Assert.Equal((6, 3, 960), (parent.Calls, parent.Jobs, parent.DurationSeconds));
        Assert.Equal("o/shop#12 Issue 12", parent.Label);
        Assert.Equal("o/shop#40", result.Rows[1].Label);   // not in the snapshot: only the reference
    }

    [Fact]
    public void By_issue_gives_a_pr_job_to_the_issue_the_pr_links_and_through_it_to_the_parent()
    {
        var issues = new[] { Issue(12), Issue(21, parent: 12) };
        var pulls = new[] { Pull(30, "o/shop#21") };
        var records = new[]
        {
            Cost("2026-03-05T09:00:00Z", "o/shop", 30, "review", 60, 1),
            Cost("2026-03-05T10:00:00Z", "o/shop", 30, "fix", 120, 2),
            Cost("2026-03-05T11:00:00Z", "o/shop", 31, "review", 60, 1),   // a PR without a link keeps its own row
        };

        var result = Build(CostGrouping.Issue, records, issues, pulls);

        Assert.Equal(["o/shop#12", "o/shop#31"], result.Rows.Select(row => row.Key));
        Assert.Equal((3, 2), (result.Rows[0].Calls, result.Rows[0].Jobs));
    }

    [Fact]
    public void A_job_without_a_known_count_adds_no_calls_and_is_counted_apart()
    {
        var records = new[]
        {
            Cost("2026-03-05T09:00:00Z", "o/shop", 1, "implement", 600, null),
            Cost("2026-03-05T10:00:00Z", "o/shop", 1, "analyze", 60, 1),
        };

        var row = Assert.Single(Build(CostGrouping.Project, records).Rows);

        Assert.Equal((1, 2, 1), (row.Calls, row.Jobs, row.JobsWithoutCount));
    }

    private static UsagePart Part(string run, string agent, string model, long nanoAiu) =>
        new(run, agent, model, new CopilotUsage(nanoAiu, 0, 0, 0, 1));

    [Fact]
    public void Usage_adds_up_per_group_and_jobs_that_ran_copilot_without_usage_are_counted_apart()
    {
        var withUsage = Cost("2026-03-05T09:00:00Z", "o/shop", 1, "analyze", 60, 1) with
        {
            Usage = new CopilotUsage(100, 1, 2, 3, 4),
        };
        var olderRun = Cost("2026-03-05T10:00:00Z", "o/shop", 2, "analyze", 60, 1);
        var breakdown = Cost("2026-03-05T11:00:00Z", "o/shop", 3, "breakdown", 1, 0);

        var row = Assert.Single(Build(CostGrouping.Project, [withUsage, olderRun, breakdown]).Rows);

        Assert.Equal(new CopilotUsage(100, 1, 2, 3, 4), row.Usage);
        Assert.Equal(1, row.JobsWithoutUsage);   // the breakdown ran no Copilot, so it misses nothing
    }

    [Fact]
    public void By_model_gives_one_row_per_model_and_agent_most_billed_first()
    {
        var implement = Cost("2026-03-05T09:00:00Z", "o/shop", 1, "implement", 600, 2) with
        {
            Parts =
            [
                Part("round-1-copilot", "main", "sonnet", 50),
                Part("round-1-copilot", "code-reviewer", "haiku", 5),
                Part("round-2-copilot", "main", "sonnet", 30),
            ],
        };
        var analyze = Cost("2026-03-05T10:00:00Z", "o/shop", 2, "analyze", 60, 1) with
        {
            Parts = [Part("copilot", "main", "opus", 200)],
        };

        var olderRun = Cost("2026-03-05T11:00:00Z", "o/shop", 3, "analyze", 60, 1);

        var result = Build(CostGrouping.Model, [implement, analyze, olderRun]);
        var rows = result.Rows;

        Assert.Equal(["opus - main", "sonnet - main", "haiku - code-reviewer"], rows.Select(row => row.Key));
        var sonnet = rows[1];
        Assert.Equal((80L, 2, 1, 600.0), (sonnet.Usage.NanoAiu, sonnet.Calls, sonnet.Jobs, sonnet.DurationSeconds));
        Assert.Equal(1, result.JobsOutsideRows);   // the older run has no numbers per model
        Assert.True(result.RowsShareJobs);        // the implement job is in the sonnet and the haiku row
    }
}
