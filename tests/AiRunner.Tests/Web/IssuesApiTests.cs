using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using AiRunner.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace AiRunner.Tests.Web;

/// <summary><c>GET /api/issues</c> on a TestServer over a temp SQLite file holding a snapshot and jobs.</summary>
public sealed class IssuesApiTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Seen = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FakeTime _time = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-issues-api-").FullName;
    private readonly RunnerStatus _status;
    private readonly RunnerConfig _config;
    private readonly SqliteStateStore _store;
    private WebApplication? _app;

    public IssuesApiTests()
    {
        _status = new RunnerStatus("laptop-1", "repos.yml", _time);
        _config = new RunnerConfig
        {
            RunnerName = "laptop-1",
            Projects =
            [
                new ProjectConfig
                {
                    Name = "shop",
                    IssuesIn = "issues",
                    Components =
                    [
                        new ComponentConfig { Name = "issues", Repo = "o/shop", Code = false },
                        new ComponentConfig { Name = "api", Repo = "o/shop-api" },
                        new ComponentConfig { Name = "web", Repo = "o/shop-web" },
                    ],
                },
                new ProjectConfig
                {
                    Name = "blog",
                    IssuesIn = "blog",
                    Components = [new ComponentConfig { Name = "blog", Repo = "o/blog" }],
                },
            ],
        };
        _store = new SqliteStateStore(Path.Combine(_folder, "state.db"), pooling: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        FileSystem.DeleteDirectory(_folder);
    }

    private async Task<HttpClient> StartAsync()
    {
        var options = new DashboardAppOptions(_config, _status, _time, Path.Combine(_folder, "wwwroot"))
        {
            Jobs = _store,
            Snapshot = _store,
        };
        var builder = DashboardApp.CreateBuilder(options);
        builder.WebHost.UseTestServer();
        _app = DashboardApp.Build(builder, options);
        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");

        return client;
    }

    private static IssueSnapshot Issue(string project, string repo, int number, params string[] labels) =>
        new(repo, number, project, $"Issue {number}", "open", labels, [], Seen, Seen)
        {
            HtmlUrl = $"http://gb/{repo}/issues/{number}",
        };

    private static IssueSnapshot Sub(int number, int order, string component, bool isOpen, params string[] labels) =>
        Issue("shop", "o/shop", number, labels) with
        {
            State = isOpen ? "open" : "closed",
            Parent = 12,
            SubOrder = order,
            Component = component,
        };

    private static PullSnapshot Pull(string repo, int number, string head, string baseRef = "main") =>
        new(repo, number, "shop", "t", "open", false, head, baseRef, "sha", Seen, Seen);

    /// <summary>
    /// shop: simple #7 with an open PR in the api repo; parent #12 with sub-issues #20 (api, closed) and #21 (web,
    /// running). blog: #3 with open questions, failed, queued.
    /// </summary>
    private void Arrange()
    {
        _store.SaveIssues(
        [
            Issue("shop", "o/shop", 7, "ai", Labels.Ready, Labels.Simple, Labels.Implementing),
            Issue("shop", "o/shop", 12, "ai", Labels.Parent),
            Sub(20, 1, "api", isOpen: false, "ai", Labels.SubIssue),
            Sub(21, 2, "web", isOpen: true, "ai", Labels.SubIssue, Labels.Implementing, Labels.Claimed),
            Issue("blog", "o/blog", 3, "ai", Labels.QuestionsOpen, Labels.Failed),
        ]);
        _store.SavePulls([Pull("o/shop-api", 50, "ai/issue-7")]);

        var running = _store.AddJob(new JobRequest(JobType.ImplementIssue, "o/shop", 21));
        _store.SetJobStatus(running, JobStatus.Running);
        _status.WorkerBusy(new RunningJob(running, "ImplementIssue", "o/shop", 21, Seen));
        _status.ClaimHeld();
        _store.AddJob(new JobRequest(JobType.AnalyzeIssue, "o/blog", 3));
    }

    private static async Task<IssueListResponse> GetListAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"api/issues{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<IssueListResponse>(Json))!;
    }

    private static IEnumerable<int> Numbers(IEnumerable<IssueRowResponse> rows) => rows.Select(row => row.Number);

    [Fact]
    public async Task Issues_come_with_stage_flags_and_badge_and_sub_issues_nested()
    {
        Arrange();
        var client = await StartAsync();

        var list = await GetListAsync(client);

        Assert.Equal([12, 7, 3], Numbers(list.Items));

        var parent = list.Items[0];
        Assert.Equal(("broken-down", "Broken down (1/2)"), (parent.Stage, parent.StageLabel));
        Assert.Equal([20, 21], Numbers(parent.SubIssues));

        var running = parent.SubIssues[1];
        Assert.Equal(("implementing", "web", "running"), (running.Stage, running.Component, running.Badge?.Code));
        Assert.Equal("Claimed by laptop-1", Assert.Single(running.Flags).Label);
        Assert.Equal(("closed-no-merge", "Closed"), (parent.SubIssues[0].Stage, parent.SubIssues[0].StateLabel));

        var simple = list.Items[1];
        Assert.Equal(("pr-open", "PR open", "Open"), (simple.Stage, simple.StageLabel, simple.StateLabel));
        Assert.Null(simple.Component);
        Assert.Equal("http://gb/o/shop/issues/7", simple.HtmlUrl);

        var blog = list.Items[2];
        Assert.Equal(("questions-open", "blog", "queued"), (blog.Stage, blog.Component, blog.Badge?.Code));
        Assert.Equal(("failed", "Failed"), (blog.Flags[0].Code, blog.Flags[0].Label));
    }

    [Fact]
    public async Task Rows_carry_their_lane_the_action_they_wait_on_sub_issue_progress_and_their_pulls()
    {
        Arrange();
        var client = await StartAsync();

        var list = await GetListAsync(client);

        var parent = list.Items[0];
        Assert.Equal(("in-progress", "In progress"), (parent.Lane, parent.LaneLabel));
        Assert.Equal((1, 2), (parent.Progress?.Closed, parent.Progress?.Total));
        Assert.Null(parent.Waiting);
        Assert.Null(parent.SubIssues[0].Progress);

        var simple = list.Items[1];
        Assert.Equal(("in-review", "review-and-merge"), (simple.Lane, simple.Waiting?.Code));
        Assert.Equal("Review and merge PR #50", simple.Waiting?.Label);
        var pull = Assert.Single(simple.Pulls);
        Assert.Equal(("o/shop-api", 50, "open"), (pull.Repo, pull.Number, pull.State));

        // Queued: the runner is on it, so nobody is waited on, even though it failed before.
        var blog = list.Items[2];
        Assert.Equal(("todo", null), (blog.Lane, blog.Waiting?.Code));
        Assert.Empty(blog.Pulls);
    }

    [Fact]
    public async Task Lanes_and_focuses_are_counted_over_every_issue_in_scope_whatever_is_chosen()
    {
        Arrange();
        var client = await StartAsync();

        var all = await GetListAsync(client);
        var chosen = await GetListAsync(client, "?lane=finished&stage=done");
        var working = await GetListAsync(client, "?focus=working");
        var blog = await GetListAsync(client, "?project=blog");

        static string Counts(IEnumerable<CountResponse> counts) =>
            string.Join(",", counts.Select(count => $"{count.Code}={count.Count}"));

        Assert.Equal("todo=1,ready=0,in-progress=2,in-review=1,finished=1", Counts(all.Lanes));
        Assert.Equal("needs-you=1,working=2,failed=1", Counts(all.Focuses));
        Assert.Equal(Counts(all.Lanes), Counts(chosen.Lanes));
        Assert.Equal(Counts(all.Focuses), Counts(chosen.Focuses));
        Assert.Equal((5, 5), (all.Total, chosen.Total));

        // Lanes follow the chosen focus, so a board column counts the cards it shows; focuses and the total do not.
        Assert.Equal("todo=1,ready=0,in-progress=1,in-review=0,finished=0", Counts(working.Lanes));
        Assert.Equal(Counts(all.Focuses), Counts(working.Focuses));
        Assert.Equal(5, working.Total);
        Assert.Equal("todo=1,ready=0,in-progress=0,in-review=0,finished=0", Counts(blog.Lanes));
        Assert.Equal("needs-you=0,working=1,failed=1", Counts(blog.Focuses));
        Assert.Equal(["To do", "Ready", "In progress", "In review", "Done"], all.Lanes.Select(lane => lane.Label));
    }

    [Fact]
    public async Task A_closed_issue_is_in_no_focus()
    {
        _store.SaveIssues([Issue("blog", "o/blog", 3, "ai", Labels.Failed) with { State = "closed" }]);
        var client = await StartAsync();

        var list = await GetListAsync(client, "?focus=failed");

        Assert.Empty(list.Items);
        Assert.All(list.Focuses, focus => Assert.Equal(0, focus.Count));
    }

    [Fact]
    public async Task Sort_by_update_puts_the_last_updated_first()
    {
        _store.SaveIssues(
        [
            Issue("blog", "o/blog", 1, "ai") with { UpdatedAt = Seen.AddHours(2) },
            Issue("blog", "o/blog", 2, "ai") with { UpdatedAt = Seen },
            Issue("blog", "o/blog", 3, "ai") with { UpdatedAt = Seen.AddHours(1) },
        ]);
        var client = await StartAsync();

        var byNumber = await GetListAsync(client, "?sort=number");
        var byUpdate = await GetListAsync(client, "?sort=updated");

        Assert.Equal([3, 2, 1], Numbers(byNumber.Items));
        Assert.Equal([1, 3, 2], Numbers(byUpdate.Items));
    }

    [Fact]
    public async Task Sub_issues_come_in_build_order_not_number_order()
    {
        _store.SaveIssues(
        [
            Issue("shop", "o/shop", 12, "ai", Labels.Parent),
            Sub(31, 1, "api", isOpen: true),
            Sub(30, 2, "web", isOpen: true),
        ]);
        var client = await StartAsync();

        var list = await GetListAsync(client);

        Assert.Equal([31, 30], Numbers(Assert.Single(list.Items).SubIssues));
    }

    [Fact]
    public async Task A_claim_of_a_running_job_that_has_not_claimed_yet_is_another_runners()
    {
        _store.SaveIssues([Issue("blog", "o/blog", 3, "ai", Labels.Claimed)]);
        var running = _store.AddJob(new JobRequest(JobType.FinishIssue, "o/blog", 3));
        _store.SetJobStatus(running, JobStatus.Running);
        _status.WorkerBusy(new RunningJob(running, "FinishIssue", "o/blog", 3, Seen));
        var client = await StartAsync();

        var row = Assert.Single((await GetListAsync(client)).Items);

        Assert.Equal(("running", "Claimed by another runner"), (row.Badge?.Code, Assert.Single(row.Flags).Label));
    }

    [Fact]
    public async Task Flat_list_has_every_issue_on_its_own()
    {
        Arrange();
        var client = await StartAsync();

        var list = await GetListAsync(client, "?flat=true");

        Assert.Equal([21, 20, 12, 7, 3], Numbers(list.Items));
        Assert.All(list.Items, row => Assert.Empty(row.SubIssues));
        Assert.Equal(12, list.Items[0].Parent);
    }

    [Theory]
    [InlineData("?stage=implementing", "12:21")]
    [InlineData("?component=web", "12:21")]
    [InlineData("?project=blog", "3")]
    [InlineData("?project=shop&component=api", "12:20")]
    [InlineData("?state=closed", "12:20")]
    [InlineData("?state=closed&flat=true", "20")]
    [InlineData("?stage=pr-open", "7")]
    [InlineData("?lane=in-progress", "12:21")]
    [InlineData("?lane=finished", "12:20")]
    [InlineData("?focus=needs-you", "7")]
    [InlineData("?focus=working", "12:21,3")]
    [InlineData("?focus=failed", "3")]
    [InlineData("?q=%2312", "12")]
    [InlineData("?q=issue%202", "12:20,21")]
    [InlineData("?q=%20%20", "12:20,21,7,3")]
    [InlineData("?state=all", "12:20,21,7,3")]
    public async Task Filters_match_issues_and_keep_the_parent_of_a_matching_sub_issue(string query, string expected)
    {
        Arrange();
        var client = await StartAsync();

        var list = await GetListAsync(client, query);

        var shown = list.Items.Select(row =>
            row.SubIssues.Count == 0
                ? $"{row.Number}"
                : $"{row.Number}:{string.Join(",", Numbers(row.SubIssues))}");
        Assert.Equal(expected, string.Join(",", shown));
    }

    [Theory]
    [InlineData("?project=nope", "Unknown project")]
    [InlineData("?stage=nope", "Unknown stage")]
    [InlineData("?state=nope", "Unknown state")]
    [InlineData("?component=nope", "Unknown component")]
    [InlineData("?project=blog&component=api", "Unknown component")]
    [InlineData("?lane=nope", "Unknown lane")]
    [InlineData("?focus=nope", "Unknown focus")]
    [InlineData("?sort=nope", "Unknown sort")]
    public async Task Unknown_filter_values_are_a_bad_request(string query, string message)
    {
        var client = await StartAsync();

        var response = await client.GetAsync($"api/issues{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>(Json);
        Assert.StartsWith(message, error!.Message);
    }

    [Fact]
    public async Task Filter_options_come_from_the_config_and_the_stage_rules()
    {
        var client = await StartAsync();

        var list = await GetListAsync(client);

        Assert.Empty(list.Items);
        Assert.Equal(
            [("shop", "api,web"), ("blog", "blog")],
            list.Projects.Select(project => (project.Name, string.Join(",", project.Components))));
        Assert.Equal(IssueStage.Stages.Select(stage => stage.Code), list.Stages.Select(stage => stage.Code));
    }
}
