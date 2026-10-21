using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using AiRunner.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Web;

/// <summary>
/// The action routes on a TestServer with the real <see cref="RunnerActions"/> over a <see cref="FakeCodeHost"/>: the
/// fresh read, the label or comment written, the 409 when the action is not offered any more, the woken poller, and
/// that nothing is put in the queue.
/// </summary>
public sealed class ActionsApiTests : IAsyncDisposable
{
    private const string IssuesRepo = "org/mbh-issues";
    private const string BackendRepo = "org/mbh-backend";
    private static readonly DateTimeOffset Seen = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FakeTime _time = new();
    private readonly FakeCodeHost _host = new();
    private readonly RunnerCommands _commands = new();
    private readonly RunnerIdentity _identity = new() { Login = "bot" };
    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-actions-api-").FullName;
    private readonly RunnerConfig _config;
    private readonly SqliteStateStore _store;
    private WebApplication? _app;

    public ActionsApiTests()
    {
        _config = new RunnerConfig
        {
            RunnerName = "laptop-1",
            Projects = [TestProjects.SeparateIssuesRepo()],
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
        var actions = new RunnerActions(
            RunnerConfigSource.Fixed(_config),
            _host,
            _store,
            _identity,
            _commands,
            _time,
            NullLogger<RunnerActions>.Instance);
        var options = new DashboardAppOptions(RunnerConfigSource.Fixed(_config), status, _time, Path.Combine(_folder, "wwwroot"))
        {
            Jobs = _store,
            Snapshot = _store,
            Commands = _commands,
            Actions = actions,
            Identity = _identity,
        };
        var builder = DashboardApp.CreateBuilder(options);
        builder.WebHost.UseTestServer();
        _app = DashboardApp.Build(builder, options);
        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");

        return client;
    }

    private static HttpRequestMessage Post(string path, object? body = null)
    {
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WriteGuard.HeaderName, "1");

        return request;
    }

    private static async Task<(HttpStatusCode Status, string Message)> SendAsync(
        HttpClient client,
        HttpRequestMessage request)
    {
        using (request)
        {
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            var body = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
            var message = body.ValueKind == JsonValueKind.Object ? body.GetProperty("message").GetString() : null;

            return (response.StatusCode, message ?? "");
        }
    }

    private Task<(HttpStatusCode Status, string Message)> IssueActionAsync(
        HttpClient client,
        int number,
        string code,
        string? text = null,
        string repo = IssuesRepo) =>
        SendAsync(client, Post($"api/issues/{repo}/{number}/actions/{code}", text is null ? null : new { text }));

    private Task<(HttpStatusCode Status, string Message)> PullActionAsync(
        HttpClient client,
        int number,
        string code,
        string? text = null,
        string repo = BackendRepo) =>
        SendAsync(client, Post($"api/pulls/{repo}/{number}/actions/{code}", text is null ? null : new { text }));

    private bool IsPollerWoken() => _commands.WaitForWakeAsync(CancellationToken.None).IsCompleted;

    private void AssertNothingWritten()
    {
        Assert.Empty(_host.AddedLabels);
        Assert.Empty(_host.RemovedLabels);
        Assert.Empty(_host.PersonCommentTargets);
        Assert.False(IsPollerWoken());
    }

    private void AssertNothingQueued()
    {
        Assert.Empty(_store.ListQueue());
        Assert.Empty(_store.ListActive());
    }

    private HostIssue ReadySimple(int number = 7) =>
        _host.AddIssue(number, "Add login", null, "ai", Labels.Ready, Labels.Simple);

    // ---- label actions ----

    [Fact]
    public async Task Implement_reads_the_issue_fresh_adds_the_label_and_wakes_the_poller()
    {
        ReadySimple();
        var client = await StartAsync();

        var (status, message) = await IssueActionAsync(client, 7, "implement");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Contains("ai-implement", message);
        Assert.Contains(Labels.Implement, _host.LabelsOf(7));
        Assert.Contains(7, _host.IssueReads);
        Assert.True(IsPollerWoken());
        AssertNothingQueued();
    }

    [Fact]
    public async Task A_code_host_failure_is_a_502_and_does_not_wake_the_poller()
    {
        ReadySimple();
        _host.AddLabelError = "no write access";
        var client = await StartAsync();

        var (status, message) = await IssueActionAsync(client, 7, "implement");

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("did not accept", message);
        Assert.False(IsPollerWoken());
        AssertNothingQueued();
    }

    [Fact]
    public async Task Breakdown_adds_ai_breakdown_to_a_ready_complex_issue()
    {
        _host.AddIssue(8, "Big", null, "ai", Labels.Ready, Labels.Complex);
        var client = await StartAsync();

        var (status, _) = await IssueActionAsync(client, 8, "breakdown");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Contains(Labels.Breakdown, _host.LabelsOf(8));
        AssertNothingQueued();
    }

    [Fact]
    public async Task Retry_removes_ai_failed()
    {
        _host.AddIssue(9, "Broke", null, "ai", Labels.Failed);
        var client = await StartAsync();

        var (status, _) = await IssueActionAsync(client, 9, "retry");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Contains((9, Labels.Failed), _host.RemovedLabels);
        Assert.True(IsPollerWoken());
    }

    [Fact]
    public async Task An_action_no_longer_offered_is_a_409_and_writes_nothing()
    {
        // The page showed "Implement", then someone added the label on the code host.
        _host.AddIssue(7, "Add login", null, "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        var client = await StartAsync();

        var (status, message) = await IssueActionAsync(client, 7, "implement");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("not available", message);
        Assert.Contains(7, _host.IssueReads);
        AssertNothingWritten();
        AssertNothingQueued();
    }

    [Fact]
    public async Task An_issue_with_a_job_is_a_409()
    {
        ReadySimple();
        _store.AddJob(new JobRequest(JobType.AnalyzeIssue, IssuesRepo, 7));
        var client = await StartAsync();

        var (status, _) = await IssueActionAsync(client, 7, "implement");

        Assert.Equal(HttpStatusCode.Conflict, status);
        AssertNothingWritten();
    }

    [Fact]
    public async Task A_closed_issue_is_a_409()
    {
        var issue = ReadySimple();
        _host.Issues[7] = issue with { State = "closed" };
        var client = await StartAsync();

        var (status, _) = await IssueActionAsync(client, 7, "implement");

        Assert.Equal(HttpStatusCode.Conflict, status);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Unknown_actions_unknown_repos_and_set_model_are_404()
    {
        ReadySimple();
        var client = await StartAsync();

        var unknownAction = await IssueActionAsync(client, 7, "explode");
        var setModel = await IssueActionAsync(client, 7, "set-model", "x");
        var unknownRepo = await IssueActionAsync(client, 7, "implement", repo: "org/elsewhere");
        var codeRepo = await IssueActionAsync(client, 7, "implement", repo: BackendRepo);

        Assert.Equal(HttpStatusCode.NotFound, unknownAction.Status);
        Assert.Equal(HttpStatusCode.NotFound, setModel.Status);
        Assert.Equal(HttpStatusCode.NotFound, unknownRepo.Status);
        Assert.Equal(HttpStatusCode.NotFound, codeRepo.Status);
        AssertNothingWritten();
    }

    // ---- comment actions ----

    [Fact]
    public async Task Answer_posts_a_people_comment_that_the_planner_takes_as_a_trigger()
    {
        _host.AddIssue(7, "Add login", null, "ai", Labels.QuestionsOpen);
        var client = await StartAsync();

        var (status, _) = await IssueActionAsync(client, 7, "answer", "Use the v2 endpoint.");

        var comment = Assert.Single(_host.CommentsOf(7));
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("@ai Use the v2 endpoint.\n\n_(sent from the ai-runner dashboard on laptop-1)_", comment.Body);
        Assert.False(BotComments.IsBot(comment));
        Assert.True(BotComments.MentionsAi(comment));
        Assert.Equal([7], _host.PersonCommentTargets);
        Assert.True(IsPollerWoken());
        AssertNothingQueued();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task A_text_action_without_a_text_is_a_400_and_writes_nothing(string? text)
    {
        _host.AddIssue(7, "Add login", null, "ai", Labels.QuestionsOpen);
        var client = await StartAsync();

        var (status, message) = await IssueActionAsync(client, 7, "answer", text);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("needs a text", message);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Ask_asks_for_a_new_analysis_round_on_a_ready_issue()
    {
        ReadySimple();
        var client = await StartAsync();

        var (status, _) = await IssueActionAsync(client, 7, "ask", "Also cover SSO.");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.StartsWith("@ai Also cover SSO.", _host.CommentsOf(7).Single().Body);
    }

    // ---- start the next sub-issue ----

    [Fact]
    public async Task Start_next_sub_issue_adds_the_label_on_the_first_open_sub_issue()
    {
        _host.AddIssue(12, "Parent", null, "ai", Labels.Parent);
        _host.AddBotComment(12, SubIssueBody.ListMarker([20, 21]));
        _host.AddIssue(20, "Sub 1", null, "ai", Labels.SubIssue, Labels.Ready, Labels.Simple);
        _host.AddIssue(21, "Sub 2", null, "ai", Labels.SubIssue, Labels.Ready, Labels.Simple);
        var client = await StartAsync();

        var (status, _) = await IssueActionAsync(client, 12, "start-next-sub-issue");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal([(20, Labels.Implement)], _host.AddedLabels);
        Assert.DoesNotContain(Labels.Implement, _host.LabelsOf(12));
        AssertNothingQueued();
    }

    [Fact]
    public async Task Start_next_sub_issue_is_a_409_while_a_sub_issue_is_in_progress()
    {
        _host.AddIssue(12, "Parent", null, "ai", Labels.Parent);
        _host.AddBotComment(12, SubIssueBody.ListMarker([20, 21]));
        _host.AddIssue(20, "Sub 1", null, "ai", Labels.SubIssue, Labels.Implementing);
        _host.AddIssue(21, "Sub 2", null, "ai", Labels.SubIssue, Labels.Ready, Labels.Simple);
        var client = await StartAsync();

        var (status, _) = await IssueActionAsync(client, 12, "start-next-sub-issue");

        Assert.Equal(HttpStatusCode.Conflict, status);
        AssertNothingWritten();
    }

    // ---- PR actions ----

    [Fact]
    public async Task Full_review_adds_the_label_on_a_pull_in_scope()
    {
        _host.AddPull(50, "feature/x", "main", repo: BackendRepo, labels: "ai");
        var client = await StartAsync();

        var (status, _) = await PullActionAsync(client, 50, "review-full");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal([(50, Labels.ReviewFull)], _host.AddedLabels);
        Assert.Contains(50, _host.RepoCalls.Where(call => call.Operation == "pull-read").Select(_ => 50));
        Assert.True(IsPollerWoken());
        AssertNothingQueued();
    }

    [Fact]
    public async Task Fix_posts_the_keyword_comment_on_the_pull()
    {
        _host.AddPull(50, "feature/x", "main", repo: BackendRepo, labels: "ai");
        var client = await StartAsync();

        var (status, _) = await PullActionAsync(client, 50, "fix", "Rename the helper.");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal([50], _host.PersonCommentTargets);
        Assert.StartsWith("@ai Rename the helper.", _host.CommentsOf(50).Single().Body);
        AssertNothingQueued();
    }

    [Fact]
    public async Task A_pull_out_of_scope_or_with_a_job_is_a_409()
    {
        _host.AddPull(50, "feature/x", "main", repo: BackendRepo);
        _host.AddPull(51, "feature/y", "main", repo: BackendRepo, labels: "ai");
        _store.AddJob(new JobRequest(JobType.ReviewPullRequest, BackendRepo, 51));
        var client = await StartAsync();

        var outOfScope = await PullActionAsync(client, 50, "review-full");
        var busy = await PullActionAsync(client, 51, "fix", "x");

        Assert.Equal(HttpStatusCode.Conflict, outOfScope.Status);
        Assert.Equal(HttpStatusCode.Conflict, busy.Status);
        AssertNothingWritten();
    }

    [Fact]
    public async Task A_pull_in_the_issues_repo_or_an_issue_action_code_on_a_pull_is_refused()
    {
        _host.AddPull(50, "feature/x", "main", repo: BackendRepo, labels: "ai");
        var client = await StartAsync();

        var issuesRepo = await PullActionAsync(client, 50, "review-full", repo: IssuesRepo);
        var issueCode = await PullActionAsync(client, 50, "implement");

        Assert.Equal(HttpStatusCode.NotFound, issuesRepo.Status);
        Assert.Equal(HttpStatusCode.Conflict, issueCode.Status);
        AssertNothingWritten();
    }

    // ---- the guard and the lists ----

    [Fact]
    public async Task The_action_routes_are_behind_the_write_guard()
    {
        ReadySimple();
        var client = await StartAsync();

        var withoutHeader = Post("api/issues/org/mbh-issues/7/actions/implement");
        withoutHeader.Headers.Remove(WriteGuard.HeaderName);
        var guarded = await SendAsync(client, withoutHeader);
        var wrongMethod = new HttpRequestMessage(HttpMethod.Put, "api/issues/org/mbh-issues/7/actions/implement");
        using var wrongMethodAnswer = await client.SendAsync(wrongMethod);

        Assert.Equal(HttpStatusCode.Forbidden, guarded.Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethodAnswer.StatusCode);
        AssertNothingWritten();
    }

    [Fact]
    public async Task The_issue_list_and_the_detail_carry_the_actions_of_each_issue_and_pull()
    {
        _store.SaveIssues(
        [
            new IssueSnapshot(IssuesRepo, 7, "mbh", "Add login", "open", [Labels.Ready, Labels.Simple, "ai"], [], Seen, Seen),
            new IssueSnapshot(IssuesRepo, 8, "mbh", "Ready", "open", [Labels.Ready, Labels.Simple, "ai"], [], Seen, Seen),
            new IssueSnapshot(IssuesRepo, 9, "mbh", "Broke", "open", [Labels.Failed, "ai"], [], Seen, Seen),
        ]);
        _store.SavePulls(
        [
            new PullSnapshot(BackendRepo, 50, "mbh", "t", "open", false, "ai/issue-7", "main", "sha", Seen, Seen)
            {
                LinkedIssue = $"{IssuesRepo}#7",
                Labels = ["ai"],
                Author = "bot",
            },
        ]);
        var client = await StartAsync();

        var list = await client.GetFromJsonAsync<IssueListResponse>("api/issues?flat=true", Json);
        var detail = await client.GetFromJsonAsync<IssueDetailResponse>($"api/issues/{IssuesRepo}/7", Json);

        var inReview = list!.Items.Single(row => row.Number == 7);
        var simple = list.Items.Single(row => row.Number == 8);
        var failed = list.Items.Single(row => row.Number == 9);
        Assert.True(
            simple.Actions.Any(action => action is { Code: "ask", NeedsText: true }),
            JsonSerializer.Serialize(simple, Json));
        Assert.Equal(["implement", "ask"], simple.Actions.Select(action => action.Code));
        Assert.Empty(inReview.Actions);
        Assert.Equal(["retry"], failed.Actions.Select(action => action.Code));
        Assert.Equal(["review-full", "fix"], detail!.Issue.Pulls.Single().Actions.Select(action => action.Code));
    }
}
