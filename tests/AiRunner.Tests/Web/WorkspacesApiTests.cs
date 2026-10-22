using System.Net;
using System.Text;
using System.Text.Json;
using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using AiRunner.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Web;

/// <summary>
/// The workspace routes on a TestServer: the list shape, the status of every outcome, and the orphan delete reached
/// through real URLs with the real <see cref="RunnerWorkspaces"/> (a boundary: the name comes from a request).
/// </summary>
public sealed class WorkspacesApiTests : IAsyncDisposable
{
    private readonly FakeTime _time = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-workspaces-api-").FullName;
    private readonly RunnerConfig _config;
    private readonly WorkspaceLocks _locks = new();
    private WebApplication? _app;

    public WorkspacesApiTests()
    {
        _config = new RunnerConfig
        {
            Paths = new PathsConfig { Work = Path.Combine(_folder, "work") },
            Projects = [TestProjects.SeparateIssuesRepo()],
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        FileSystem.DeleteDirectory(_folder);
    }

    private async Task<HttpClient> StartAsync(IRunnerWorkspaces workspaces)
    {
        var status = new RunnerStatus("laptop-1", "repos.yml", _time);
        var options = new DashboardAppOptions(
            RunnerConfigSource.Fixed(_config),
            status,
            _time,
            Path.Combine(_folder, "wwwroot"))
        {
            Workspaces = workspaces,
        };
        var builder = DashboardApp.CreateBuilder(options);
        builder.WebHost.UseTestServer();
        _app = DashboardApp.Build(builder, options);
        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");

        return client;
    }

    private IRunnerWorkspaces Real()
    {
        var inspector = new WorkspaceInspector(
            RunnerConfigSource.Fixed(_config),
            new FakeGit(),
            _locks,
            new DirectorySizes(_time));

        return new RunnerWorkspaces(
            RunnerConfigSource.Fixed(_config),
            inspector,
            new FakeGit(),
            new FakeWorkspace(),
            _locks,
            NullLogger<RunnerWorkspaces>.Instance);
    }

    private static HttpRequestMessage Write(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WriteGuard.HeaderName, "1");

        return request;
    }

    [Fact]
    public async Task The_list_has_every_project_with_its_clones_and_the_orphans()
    {
        var head = new CloneHead(
            "abc123",
            "init",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)),
            null,
            "origin/main");
        var clone = new CloneInfo(
            "org/mbh-backend",
            "mbh-backend",
            true,
            ["backend"],
            head,
            new DateTimeOffset(2026, 1, 2, 4, 0, 0, TimeSpan.Zero),
            ["a.txt"],
            2048,
            [new WorkspaceWarning("has-changes", "one file changed")]);
        var listing = new WorkspaceListing(
            [new ProjectWorkspace("mbh", true, [clone])],
            [new OrphanFolder("old-project", null)]);
        var client = await StartAsync(new StubWorkspaces(listing));

        using var response = await client.GetAsync("api/workspaces");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var project = json.GetProperty("projects")[0];
        Assert.Equal("mbh", project.GetProperty("name").GetString());
        Assert.True(project.GetProperty("isBusy").GetBoolean());
        var cloneJson = project.GetProperty("clones")[0];
        Assert.Equal("mbh-backend", cloneJson.GetProperty("folder").GetString());
        Assert.Equal("origin/main", cloneJson.GetProperty("detachedAt").GetString());
        Assert.Equal(JsonValueKind.Null, cloneJson.GetProperty("branch").ValueKind);
        Assert.Equal("2026-01-02T01:04:05+00:00", cloneJson.GetProperty("headAt").GetString());
        Assert.Equal(2048, cloneJson.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("has-changes", cloneJson.GetProperty("warnings")[0].GetProperty("code").GetString());
        var orphan = json.GetProperty("orphans")[0];
        Assert.Equal("old-project", orphan.GetProperty("folder").GetString());
        Assert.Equal(JsonValueKind.Null, orphan.GetProperty("sizeBytes").ValueKind);
    }

    [Theory]
    [InlineData(WorkspaceOutcome.Done, HttpStatusCode.Accepted)]
    [InlineData(WorkspaceOutcome.NotFound, HttpStatusCode.NotFound)]
    [InlineData(WorkspaceOutcome.Refused, HttpStatusCode.BadRequest)]
    [InlineData(WorkspaceOutcome.Busy, HttpStatusCode.Conflict)]
    [InlineData(WorkspaceOutcome.Failed, HttpStatusCode.BadGateway)]
    public async Task Every_outcome_has_its_status_and_the_message_reaches_the_page(
        WorkspaceOutcome outcome,
        HttpStatusCode status)
    {
        var stub = new StubWorkspaces(new WorkspaceListing([], [])) { Reply = new WorkspaceReply(outcome, "the text") };
        var client = await StartAsync(stub);
        (HttpMethod Method, string Path)[] routes =
        [
            (HttpMethod.Post, "api/workspaces/mbh/mbh-backend/fetch"),
            (HttpMethod.Post, "api/workspaces/mbh/mbh-backend/reclone"),
            (HttpMethod.Delete, "api/workspaces/orphans/old-project"),
        ];

        foreach (var (method, path) in routes)
        {
            using var request = Write(method, path);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            Assert.Equal(status, response.StatusCode);
            Assert.Contains("the text", text);
        }

        Assert.Equal(
            ["fetch mbh/mbh-backend", "reclone mbh/mbh-backend", "orphan old-project"],
            stub.Calls);
    }

    [Fact]
    public async Task A_fetch_or_reclone_under_the_project_lock_is_409()
    {
        var client = await StartAsync(Real());
        await _locks.HoldForJobAsync("mbh", default);

        foreach (var action in new[] { "fetch", "reclone" })
        {
            using var request = Write(HttpMethod.Post, $"api/workspaces/mbh/mbh-backend/{action}");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        _locks.ReleaseJobHolds();
    }

    [Theory]
    [InlineData("%2E%2E")]
    [InlineData("..%2Fkeep")]
    [InlineData("..%5Ckeep")]
    [InlineData("a%2Fb")]
    [InlineData("a%5Cb")]
    [InlineData("C%3A%5CWindows")]
    [InlineData("%2Fetc")]
    [InlineData("mbh")]
    [InlineData("MBH")]
    [InlineData("%2Eno-hooks")]
    public async Task Orphan_delete_through_a_url_refuses_every_bad_name_and_deletes_nothing(string folder)
    {
        var keep = Path.Combine(_folder, "keep");
        Directory.CreateDirectory(keep);
        var project = Path.Combine(_folder, "work", "mbh");
        Directory.CreateDirectory(project);
        var client = await StartAsync(Real());

        using var request = Write(HttpMethod.Delete, $"api/workspaces/orphans/{folder}");
        using var response = await client.SendAsync(request);

        // "%2E%2E" is normalised to ".." by the URL parser before it is sent, so the route is never reached (405).
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
                or HttpStatusCode.MethodNotAllowed,
            $"status was {response.StatusCode}");
        Assert.True(Directory.Exists(keep));
        Assert.True(Directory.Exists(project));
        Assert.True(Directory.Exists(Path.Combine(_folder, "work")));
    }

    [Fact]
    public async Task Orphan_delete_through_a_url_removes_a_plain_orphan()
    {
        var orphan = Path.Combine(_folder, "work", "old-project");
        Directory.CreateDirectory(orphan);
        var client = await StartAsync(Real());

        using var request = Write(HttpMethod.Delete, "api/workspaces/orphans/old-project");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(Directory.Exists(orphan));
    }

    private sealed class StubWorkspaces(WorkspaceListing listing) : IRunnerWorkspaces
    {
        public WorkspaceReply Reply { get; init; } = new(WorkspaceOutcome.Done, "ok");

        public List<string> Calls { get; } = [];

        public Task<WorkspaceListing> ListAsync(CancellationToken cancellationToken) => Task.FromResult(listing);

        public Task<WorkspaceReply> FetchAsync(string project, string folder, CancellationToken cancellationToken)
        {
            Calls.Add($"fetch {project}/{folder}");

            return Task.FromResult(Reply);
        }

        public Task<WorkspaceReply> RecloneAsync(string project, string folder, CancellationToken cancellationToken)
        {
            Calls.Add($"reclone {project}/{folder}");

            return Task.FromResult(Reply);
        }

        public Task<WorkspaceReply> DeleteOrphanAsync(string folder, CancellationToken cancellationToken)
        {
            Calls.Add($"orphan {folder}");

            return Task.FromResult(Reply);
        }
    }
}
