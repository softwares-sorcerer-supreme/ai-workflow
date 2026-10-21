using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using AiRunner.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace AiRunner.Tests.Web;

/// <summary>
/// The CSRF write guard and the runtime-control routes. The guard is a security boundary (any web page the user opens
/// can send a request to 127.0.0.1), so every refusal has its own test.
/// </summary>
public sealed class WriteGuardTests : IAsyncDisposable
{
    private const int Port = 5180;
    private const string PollRoute = "api/runner/poll";

    private readonly FakeTime _time = new();
    private readonly RunnerStatus _status;
    private readonly RunnerCommands _commands;
    private readonly RunnerConfig _config = new() { Dashboard = new DashboardConfig { Port = Port } };
    private readonly string _webRoot = Directory.CreateTempSubdirectory("ai-runner-ui-").FullName;
    private WebApplication? _app;

    public WriteGuardTests()
    {
        _status = new RunnerStatus("laptop-1", "repos.yml", _time);
        _commands = new RunnerCommands();
    }

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
        var options = new DashboardAppOptions(RunnerConfigSource.Fixed(_config), _status, _time, _webRoot) { Commands = _commands };
        var builder = DashboardApp.CreateBuilder(options);
        builder.WebHost.UseTestServer();
        _app = DashboardApp.Build(builder, options);
        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");

        return client;
    }

    /// <summary>A write as the dashboard sends it: the header, a JSON body and, when given, an Origin.</summary>
    private static HttpRequestMessage Write(string path, string? origin = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WriteGuard.HeaderName, "1");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }

    private static async Task<string> MessageOf(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        return document.RootElement.GetProperty("message").GetString() ?? "";
    }

    private bool IsWakePending() => _commands.WaitForWakeAsync(default).IsCompleted;

    // ---- refusals ----

    [Fact]
    public async Task A_write_without_the_header_is_403_and_does_nothing()
    {
        var client = await StartAsync();
        using var request = Write(PollRoute);
        request.Headers.Remove(WriteGuard.HeaderName);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(IsWakePending());
    }

    [Theory]
    [InlineData("PUT", "api/setup/token")]
    [InlineData("DELETE", "api/setup/token")]
    [InlineData("PUT", "api/setup/web-password")]
    [InlineData("DELETE", "api/setup/web-password")]
    [InlineData("POST", "api/runner/pause")]
    [InlineData("POST", "api/runner/resume")]
    [InlineData("POST", "api/jobs/12/cancel")]
    [InlineData("POST", "api/workspaces/mbh/backend/fetch")]
    [InlineData("POST", "api/workspaces/mbh/backend/reclone")]
    [InlineData("DELETE", "api/workspaces/orphans/old-project")]
    [InlineData("PUT", "api/agents/file?path=agents/a.agent.md")]
    [InlineData("POST", "api/agents/revert?path=agents/a.agent.md")]
    [InlineData("POST", "api/agents/sync")]
    public async Task Every_write_route_without_the_header_is_403(string method, string path)
    {
        var client = await StartAsync();
        using var request = Write(path, method: new HttpMethod(method));
        request.Headers.Remove(WriteGuard.HeaderName);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("PUT", "api/setup/token")]
    [InlineData("DELETE", "api/setup/web-password")]
    [InlineData("POST", "api/workspaces/mbh/backend/reclone")]
    [InlineData("DELETE", "api/workspaces/orphans/old-project")]
    [InlineData("PUT", "api/agents/file?path=agents/a.agent.md")]
    [InlineData("POST", "api/agents/sync")]
    public async Task A_setup_write_from_a_foreign_origin_is_403(string method, string path)
    {
        var client = await StartAsync();
        using var request = Write(path, "http://evil.example", new HttpMethod(method));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_write_with_another_header_value_is_403()
    {
        var client = await StartAsync();
        using var request = Write(PollRoute);
        request.Headers.Remove(WriteGuard.HeaderName);
        request.Headers.Add(WriteGuard.HeaderName, "true");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost:4200")]
    [InlineData("https://localhost:5180")]
    [InlineData("http://localhost.evil.example:5180")]
    [InlineData("null")]
    public async Task A_write_from_a_foreign_origin_is_403(string origin)
    {
        var client = await StartAsync();
        using var request = Write(PollRoute, origin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(IsWakePending());
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded", "a=1")]
    [InlineData("multipart/form-data; boundary=x", "--x--")]
    [InlineData("text/plain", "{}")]
    public async Task A_write_with_a_form_or_text_body_is_403(string contentType, string body)
    {
        var client = await StartAsync();
        using var request = Write(PollRoute);
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_write_without_a_content_type_is_403()
    {
        var client = await StartAsync();
        using var request = Write(PollRoute);
        request.Content = null;

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_write_from_a_non_loopback_address_is_403()
    {
        await StartAsync();
        var server = _app!.GetTestServer();

        var context = await server.SendAsync(httpContext =>
        {
            httpContext.Request.Method = HttpMethods.Post;
            httpContext.Request.Path = "/" + PollRoute;
            httpContext.Request.Host = new HostString("localhost");
            httpContext.Request.ContentType = "application/json";
            httpContext.Request.Headers[WriteGuard.HeaderName] = "1";
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20");
        });

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_write_with_a_foreign_host_is_refused_by_the_host_filter()
    {
        var client = await StartAsync();
        using var request = Write(PollRoute);
        request.Headers.Host = "evil.example";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(IsWakePending());
    }

    [Theory]
    [InlineData("PUT", PollRoute)]
    [InlineData("DELETE", PollRoute)]
    [InlineData("POST", "api/health")]
    [InlineData("POST", "api/nothing-here")]
    [InlineData("POST", "")]
    [InlineData("PATCH", PollRoute)]
    [InlineData("HEAD", PollRoute)]
    public async Task An_unmapped_method_is_405_even_with_the_header(string method, string path)
    {
        var client = await StartAsync();
        using var request = Write(path, method: new HttpMethod(method));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task A_get_needs_no_header()
    {
        var client = await StartAsync();

        using var response = await client.GetAsync("api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- the right request ----

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:5180")]
    [InlineData("http://127.0.0.1:5180")]
    public async Task The_right_request_passes(string? origin)
    {
        var client = await StartAsync();
        using var request = Write(PollRoute, origin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(IsWakePending());
    }

    [Fact]
    public async Task A_json_content_type_with_a_charset_passes()
    {
        var client = await StartAsync();
        using var request = Write(PollRoute);
        request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-8");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // ---- the routes ----

    [Fact]
    public async Task Pause_and_resume_show_in_the_health()
    {
        _status.PollStarted();
        _status.PollEnded();
        var client = await StartAsync();

        using var pause = Write("api/runner/pause");
        using var paused = await client.SendAsync(pause);
        var pausedHealth = await client.GetStringAsync("api/health");

        using var resume = Write("api/runner/resume");
        using var resumed = await client.SendAsync(resume);
        var resumedHealth = await client.GetStringAsync("api/health");

        Assert.Equal(HttpStatusCode.Accepted, paused.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.Contains("\"isPaused\":true", pausedHealth);
        Assert.Contains("\"state\":\"paused\"", pausedHealth);
        Assert.Contains("\"isPaused\":false", resumedHealth);
        Assert.False(_commands.IsPaused);
    }

    [Fact]
    public async Task Poll_now_answers_with_a_message()
    {
        var client = await StartAsync();
        using var request = Write(PollRoute);

        using var response = await client.SendAsync(request);

        Assert.False(string.IsNullOrWhiteSpace(await MessageOf(response)));
    }

    [Fact]
    public async Task Cancelling_a_job_that_is_not_running_is_409()
    {
        var client = await StartAsync();
        using var request = Write("api/jobs/12/cancel");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("not running", await MessageOf(response));
    }

    [Fact]
    public async Task Cancelling_the_running_job_cancels_its_token()
    {
        using var source = new CancellationTokenSource();
        _commands.RunStarted(12, source);
        var client = await StartAsync();
        using var request = Write("api/jobs/12/cancel");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(source.IsCancellationRequested);
        Assert.True(_commands.RunEnded(12));
    }
}
