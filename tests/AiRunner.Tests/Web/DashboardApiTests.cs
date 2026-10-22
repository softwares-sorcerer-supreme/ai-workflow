using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using AiRunner.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace AiRunner.Tests.Web;

/// <summary>The dashboard web app on a TestServer: routes, read-only, host filter, static UI or "not built" page.</summary>
public sealed class DashboardApiTests : IAsyncDisposable
{
    private readonly FakeTime _time = new();
    private readonly RunnerStatus _status;
    private readonly RunnerConfig _config = new() { PollIntervalSeconds = 60 };
    private readonly string _webRoot = Directory.CreateTempSubdirectory("ai-runner-ui-").FullName;
    private WebApplication? _app;

    public DashboardApiTests() => _status = new RunnerStatus("laptop-1", @"C:\runner\repos.yml", _time);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        FileSystem.DeleteDirectory(_webRoot);
    }

    private async Task<HttpClient> StartAsync()
    {
        var options = new DashboardAppOptions(RunnerConfigSource.Fixed(_config), _status, _time, _webRoot);
        var builder = DashboardApp.CreateBuilder(options);
        builder.WebHost.UseTestServer();
        _app = DashboardApp.Build(builder, options);
        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");

        return client;
    }

    [Fact]
    public async Task Health_returns_the_runner_status_as_codes_and_labels()
    {
        _status.PollStarted();
        _time.Advance(TimeSpan.FromSeconds(5));
        _status.ProjectFailed("shop", "issue poll failed: connection refused");
        _status.PollEnded();
        _status.WorkerBusy();
        var client = await StartAsync();

        var json = await client.GetStringAsync("api/health");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("error", root.GetProperty("state").GetString());
        Assert.Equal("Poll failed in 1 project", root.GetProperty("stateLabel").GetString());
        Assert.Equal("laptop-1", root.GetProperty("runnerName").GetString());
        Assert.Equal(@"C:\runner\repos.yml", root.GetProperty("configPath").GetString());
        Assert.Equal("unknown", root.GetProperty("copilotVersion").GetString());
        Assert.Equal(60, root.GetProperty("pollIntervalSeconds").GetInt32());
        Assert.False(root.GetProperty("isPolling").GetBoolean());
        Assert.Equal("2026-01-01T12:00:05+00:00", root.GetProperty("lastPollEndedAt").GetString());
        Assert.Equal("busy", root.GetProperty("worker").GetProperty("state").GetString());
        Assert.Equal("1 poll error", root.GetProperty("errorsLabel").GetString());
        var error = Assert.Single(root.GetProperty("errors").EnumerateArray());
        Assert.Equal("shop", error.GetProperty("project").GetString());
    }

    [Fact]
    public async Task Health_is_not_stale_while_a_long_poll_is_still_running()
    {
        _status.PollStarted();
        _status.PollEnded();
        _time.Advance(TimeSpan.FromMinutes(5));
        _status.PollStarted();
        _time.Advance(TimeSpan.FromMinutes(5));
        var client = await StartAsync();

        var json = await client.GetStringAsync("api/health");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("isPolling").GetBoolean());
        Assert.Equal("ok", root.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_foreign_host_header_is_refused()
    {
        var client = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/health");
        request.Headers.Host = "evil.example";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_loopback_address_is_an_allowed_host()
    {
        var client = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/health");
        request.Headers.Host = "127.0.0.1:5180";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "api/health")]
    [InlineData("PUT", "api/health")]
    [InlineData("DELETE", "api/health")]
    [InlineData("HEAD", "api/health")]
    [InlineData("POST", "")]
    public async Task Anything_but_get_is_405(string method, string path)
    {
        var client = await StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_api_route_is_404_not_the_ui()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<html>ui</html>");
        var client = await StartAsync();

        using var response = await client.GetAsync("api/nothing-here");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("issues")]
    public async Task Without_a_built_ui_every_page_explains_how_to_build_it(string path)
    {
        var client = await StartAsync();

        var html = await client.GetStringAsync(path);

        Assert.Contains("Dashboard not built", html);
        Assert.Contains("./build-dashboard.ps1", html);
    }

    [Theory]
    [InlineData("")]
    [InlineData("issues")]
    [InlineData("jobs/42")]
    public async Task A_built_ui_is_served_with_its_index_as_fallback(string path)
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<html>ui</html>");
        var client = await StartAsync();

        var html = await client.GetStringAsync(path);

        Assert.Equal("<html>ui</html>", html);
    }

    [Fact]
    public async Task Files_of_the_built_ui_are_served()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<html>ui</html>");
        File.WriteAllText(Path.Combine(_webRoot, "main.js"), "console.log(1)");
        var client = await StartAsync();

        var script = await client.GetStringAsync("main.js");

        Assert.Equal("console.log(1)", script);
    }

    [Fact]
    public async Task The_openapi_document_lists_the_health_route_and_matches_the_export()
    {
        var client = await StartAsync();

        var served = await client.GetStringAsync("openapi/v1.json");
        var exported = await OpenApiExport.GetDocumentAsync(_app!.Services, default);

        Assert.Contains("/api/health", served);
        Assert.Contains("HealthResponse", served);
        Assert.Equal(Normalize(served), Normalize(exported));
    }

    [Fact]
    public async Task The_export_writes_the_document_without_config_or_token()
    {
        var file = Path.Combine(_webRoot, "out", "openapi.json");

        await OpenApiExport.WriteAsync(file, default);

        Assert.Contains("/api/health", File.ReadAllText(file));
    }

    /// <summary>Compact JSON without "servers", which the served document fills from the request.</summary>
    private static string Normalize(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("servers");

        return node.ToJsonString();
    }
}
