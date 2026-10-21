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

/// <summary>
/// The waiting-on list of <c>GET /api/overview</c> and <c>GET /api/issues/{owner}/{repo}/{number}</c> on a TestServer
/// over a temp SQLite file holding a snapshot and jobs.
/// </summary>
public sealed class IssueDetailApiTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Seen = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FakeTime _time = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-detail-api-").FullName;
    private readonly RunnerStatus _status;
    private readonly RunnerConfig _config;
    private readonly SqliteStateStore _store;
    private WebApplication? _app;

    public IssueDetailApiTests()
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

    private static IssueSnapshot Issue(int number, DateTimeOffset updatedAt, params string[] labels) =>
        new("o/shop", number, "shop", $"Issue {number}", "open", labels, [], updatedAt, Seen)
        {
            HtmlUrl = $"http://gb/o/shop/issues/{number}",
        };

    private static IssueSnapshot Sub(int number, int order, string component, bool isOpen, params string[] labels) =>
        Issue(number, Seen, labels) with
        {
            State = isOpen ? "open" : "closed",
            Parent = 12,
            SubOrder = order,
            Component = component,
        };

    private static PullSnapshot Pull(string repo, int number, string head, string baseRef = "main") =>
        new(repo, number, "shop", $"PR {number}", "open", false, head, baseRef, "sha", Seen, Seen)
        {
            LinkedIssue = null,
        };

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    // ---- overview: waiting on you ----

    [Fact]
    public async Task Overview_lists_the_open_issues_that_wait_on_a_human_longest_waiting_first()
    {
        var older = Seen.AddDays(-3);
        _store.SaveIssues(
        [
            Issue(7, Seen, "ai", Labels.Ready, Labels.Simple),
            Issue(8, older, "ai", Labels.QuestionsOpen),
            Issue(9, older, "ai", Labels.QuestionsOpen),
            Issue(10, older, "ai", Labels.Implementing),
        ]);
        _store.AddJob(new JobRequest(JobType.AnalyzeIssue, "o/shop", 9));
        var client = await StartAsync();

        var overview = await GetAsync<OverviewResponse>(client, "api/overview");

        Assert.Equal([8, 7], overview.WaitingOn.Select(item => item.Number));
        Assert.Equal("2 issues wait on you", overview.WaitingLabel);
        Assert.Equal(
            ("answer-questions", "Answer the questions with @ai"),
            (overview.WaitingOn[0].Action.Code, overview.WaitingOn[0].Action.Label));
        Assert.Equal("add-ai-implement", overview.WaitingOn[1].Action.Code);
        Assert.Equal("questions-open", overview.WaitingOn[0].Stage.Code);
    }

    [Fact]
    public async Task Overview_with_nothing_waiting_says_so()
    {
        var client = await StartAsync();

        var overview = await GetAsync<OverviewResponse>(client, "api/overview");

        Assert.Empty(overview.WaitingOn);
        Assert.Equal("Nothing waits on you", overview.WaitingLabel);
    }

    // ---- issue detail ----

    [Fact]
    public async Task Detail_of_a_simple_issue_has_stage_waiting_analysis_pr_and_job_timeline()
    {
        _store.SaveIssues([Issue(7, Seen, "ai", Labels.Ready, Labels.Simple, Labels.Implementing)]);
        _store.SavePulls([Pull("o/shop-api", 50, "ai/issue-7"), Pull("o/shop-api", 51, "ai/issue-99")]);
        var implement = _store.AddJob(new JobRequest(JobType.ImplementIssue, "o/shop", 7));
        _store.SetJobStatus(implement, JobStatus.Done);
        var review = _store.AddJob(new JobRequest(JobType.ReviewPullRequest, "o/shop-api", 50));
        _store.SetJobStatus(review, JobStatus.Done);
        var otherIssue = _store.AddJob(new JobRequest(JobType.AnalyzeIssue, "o/shop", 8));
        _store.SetJobStatus(otherIssue, JobStatus.Done);
        var client = await StartAsync();

        var detail = await GetAsync<IssueDetailResponse>(client, "api/issues/o/shop/7");

        Assert.Equal(("pr-open", "PR open"), (detail.Issue.Stage, detail.Issue.StageLabel));
        Assert.Equal("Review and merge PR #50", detail.Issue.Waiting?.Label);
        Assert.Equal(("ready", "simple"), (detail.Analysis.Status.Code, detail.Analysis.Complexity.Code));
        Assert.Equal([50], detail.Issue.Pulls.Select(pull => pull.Number));
        Assert.Equal("Open", detail.Issue.Pulls[0].StateLabel);
        Assert.Equal([review, implement], detail.Timeline.Select(job => job.Id));
        Assert.Empty(detail.SubIssues);
    }

    [Fact]
    public async Task Detail_of_a_parent_lists_its_sub_issues_with_their_stage_and_prs()
    {
        _store.SaveIssues(
        [
            Issue(12, Seen, "ai", Labels.Parent),
            Sub(20, 1, "api", isOpen: false, "ai", Labels.SubIssue),
            Sub(21, 2, "web", isOpen: true, "ai", Labels.SubIssue),
        ]);
        _store.SavePulls(
        [
            Pull("o/shop-api", 60, "ai/issue-12-1", "feat/issue-12") with
            {
                State = "closed",
                IsMerged = true
            },
        ]);
        var client = await StartAsync();

        var detail = await GetAsync<IssueDetailResponse>(client, "api/issues/o/shop/12");

        Assert.Equal("Broken down (1/2)", detail.Issue.StageLabel);
        Assert.Equal("Start the next sub-issue", detail.Issue.Waiting?.Label);
        Assert.Equal([20, 21], detail.SubIssues.Select(sub => sub.Number));
        Assert.Equal(["done", "new"], detail.SubIssues.Select(sub => sub.Stage));
        Assert.Equal([60], detail.SubIssues[0].Pulls.Select(pull => pull.Number));
        Assert.Equal("Merged", detail.SubIssues[0].Pulls[0].StateLabel);
        Assert.Empty(detail.SubIssues[1].Pulls);
    }

    [Fact]
    public async Task Auto_start_on_means_a_broken_down_parent_waits_on_nobody()
    {
        _config.Projects[0].Settings.AutoStartFirstSubIssue = true;
        _store.SaveIssues([Issue(12, Seen, "ai", Labels.Parent), Sub(21, 1, "web", isOpen: true, "ai", Labels.SubIssue)]);
        var client = await StartAsync();

        var detail = await GetAsync<IssueDetailResponse>(client, "api/issues/o/shop/12");

        Assert.Null(detail.Issue.Waiting);
    }

    [Fact]
    public async Task Detail_of_an_issue_without_a_snapshot_row_is_404()
    {
        _store.SaveIssues([Issue(7, Seen, "ai")]);
        var client = await StartAsync();

        var missing = await client.GetAsync("api/issues/o/shop/999");
        var otherRepo = await client.GetAsync("api/issues/o/elsewhere/7");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherRepo.StatusCode);
    }
}
