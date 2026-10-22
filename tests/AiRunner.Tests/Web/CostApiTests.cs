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

/// <summary><c>GET /api/cost</c> on a TestServer over a temp SQLite file holding cost rows and a snapshot.</summary>
public sealed class CostApiTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Seen = new(2026, 3, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FakeTime _time = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-cost-api-").FullName;
    private readonly RunnerConfig _config;
    private readonly SqliteStateStore _store;
    private WebApplication? _app;

    public CostApiTests()
    {
        _config = new RunnerConfig
        {
            RunnerName = "laptop-1",
            Projects =
            [
                new ProjectConfig
                {
                    Name = "shop",
                    IssuesIn = "web",
                    Components = [new ComponentConfig { Name = "web", Repo = "o/shop" }],
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
        var status = new RunnerStatus("laptop-1", "repos.yml", _time);
        var options = new DashboardAppOptions(
            RunnerConfigSource.Fixed(_config),
            status,
            _time,
            Path.Combine(_folder, "wwwroot"))
        {
            Jobs = _store,
            Snapshot = _store,
            Cost = _store,
        };
        var builder = DashboardApp.CreateBuilder(options);
        builder.WebHost.UseTestServer();
        _app = DashboardApp.Build(builder, options);
        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");

        return client;
    }

    private void AddCost(string at, int number, string workflow, double seconds, int? calls, string repo = "o/shop")
    {
        var entry = new CostEntry(DateTimeOffset.Parse(at), repo, number, workflow, seconds, "ok", "", calls);
        _store.AddCost(entry);
    }

    private static IssueSnapshot Issue(int number, int? parent = null) =>
        new("o/shop", number, "shop", $"Issue {number}", "open", ["ai"], [], Seen, Seen) { Parent = parent };

    private void Arrange()
    {
        _store.SaveIssues([Issue(12), Issue(21, parent: 12)]);
        AddCost("2026-03-04T09:00:00Z", 12, "analyze", 60, 1);
        AddCost("2026-03-05T10:00:00Z", 21, "implement", 600, 3);
        AddCost("2026-03-05T11:00:00Z", 21, "implement", 300, null);   // a failed job: count unknown
        AddCost("2026-03-05T12:00:00Z", 5, "analyze", 20, 1, repo: "o/elsewhere");
    }

    private static async Task<CostResponse> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<CostResponse>(Json))!;
    }

    [Fact]
    public async Task By_day_is_the_default_and_lists_days_oldest_first()
    {
        Arrange();
        var client = await StartAsync();

        var cost = await GetAsync(client, "/api/cost");

        Assert.Equal("day", cost.By);
        Assert.Equal(["2026-03-04", "2026-03-05"], cost.Rows.Select(row => row.Key));
        Assert.Equal((4, 5), (cost.Total.Jobs, cost.Total.Calls));
        Assert.Equal(1, cost.Total.JobsWithoutCount);
        Assert.Contains("1 job", cost.Note);
    }

    [Fact]
    public async Task By_model_lists_what_each_agent_spent_and_the_rows_carry_billed_and_tokens()
    {
        var reviewerUsage = new CopilotUsage(10, 1, 2, 3, 4);
        var mainUsage = new CopilotUsage(90, 5, 6, 7, 8);
        var parts = new[]
        {
            new UsagePart("round-1-copilot", "main", "sonnet", mainUsage),
            new UsagePart("round-1-copilot", "code-reviewer", "haiku", reviewerUsage),
        };
        var usage = new JobUsage(mainUsage.Add(reviewerUsage), parts);
        var at = DateTimeOffset.Parse("2026-03-05T10:00:00Z");
        var entry = new CostEntry(at, "o/shop", 21, "implement", 600, "ok", "", 1)
        {
            Usage = usage,
        };
        _store.AddCost(entry);
        var client = await StartAsync();

        var cost = await GetAsync(client, "/api/cost?by=model");

        Assert.Equal("model", cost.By);
        Assert.Equal(["sonnet - main", "haiku - code-reviewer"], cost.Rows.Select(row => row.Key));
        var total = cost.Total;
        Assert.Equal((100L, 6L, 8L, 10L, 12L), (total.NanoAiu, total.InputTokens, total.CacheReadTokens,
            total.CacheWriteTokens, total.OutputTokens));
        Assert.Equal(
            "A job counts in each row it used, so jobs and job time add up to more than the total.",
            cost.Note);
    }

    [Fact]
    public async Task By_project_groups_by_project_and_unknown_repos_as_other()
    {
        Arrange();
        var client = await StartAsync();

        var cost = await GetAsync(client, "/api/cost?by=project");

        Assert.Equal(["shop", "(other)"], cost.Rows.Select(row => row.Key));
        Assert.Equal(4, cost.Rows[0].Calls);
    }

    [Fact]
    public async Task By_issue_includes_the_sub_issues_in_their_parent()
    {
        Arrange();
        var client = await StartAsync();

        var cost = await GetAsync(client, "/api/cost?by=issue&from=2026-03-04&to=2026-03-05");

        var parent = cost.Rows.First(row => row.Key == "o/shop#12");
        Assert.Equal((4, 3, 960), (parent.Calls, parent.Jobs, parent.DurationSeconds));
        Assert.DoesNotContain(cost.Rows, row => row.Key == "o/shop#21");
    }

    [Fact]
    public async Task From_and_to_limit_the_days()
    {
        Arrange();
        var client = await StartAsync();

        var cost = await GetAsync(client, "/api/cost?by=day&from=2026-03-05");

        Assert.Equal(["2026-03-05"], cost.Rows.Select(row => row.Key));
        Assert.Equal("2026-03-05", cost.From);
        Assert.Null(cost.To);
    }

    [Theory]
    [InlineData("/api/cost?by=week")]
    [InlineData("/api/cost?from=yesterday")]
    [InlineData("/api/cost?to=2026-13-40")]
    [InlineData("/api/cost?from=2026-03-05&to=2026-03-01")]
    public async Task A_bad_query_is_a_400_with_a_sentence(string url)
    {
        var client = await StartAsync();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>(Json);
        Assert.False(string.IsNullOrWhiteSpace(error!.Message));
    }

    [Fact]
    public async Task An_empty_log_gives_no_rows_and_a_zero_total()
    {
        var client = await StartAsync();

        var cost = await GetAsync(client, "/api/cost?by=issue");

        Assert.Empty(cost.Rows);
        Assert.Equal((0, 0), (cost.Total.Jobs, cost.Total.Calls));
        Assert.Null(cost.Note);
    }

    [Fact]
    public async Task Only_get_is_allowed()
    {
        var client = await StartAsync();

        var response = await client.PostAsync("/api/cost", null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
