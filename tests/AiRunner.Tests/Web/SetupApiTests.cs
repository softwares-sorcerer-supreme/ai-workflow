using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;
using AiRunner.Tests.Fakes;
using AiRunner.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace AiRunner.Tests.Web;

/// <summary>
/// The Setup page's routes on a TestServer, over a real <see cref="RunnerSetup"/> with a temp secrets file and a
/// <see cref="FakeCodeHost"/> behind the check. Secrets never leave the runner: no answer and no log line holds one.
/// </summary>
public sealed class SetupApiTests : IAsyncDisposable
{
    private const string Token = "tok-SECRET-4711";
    private const string WebPassword = SetupFixture.RightWebPassword;

    private SetupFixture _setup = new();
    private readonly FakeTime _time = new();
    private readonly RunnerIdentity _identity = new();
    private readonly RunnerCommands _commands = new();
    private readonly string _webRoot = Directory.CreateTempSubdirectory("ai-runner-ui-").FullName;
    private WebApplication? _app;

    /// <summary>Every answer body the test read, for the leak check.</summary>
    private readonly List<string> _answers = [];

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _setup.Dispose();
        FileSystem.DeleteDirectory(_webRoot);
    }

    private async Task<HttpClient> StartAsync(SetupFixture? setup = null)
    {
        if (setup is not null)
        {
            _setup.Dispose();
            _setup = setup;
        }

        var status = new RunnerStatus("laptop-1", "repos.yml", _time);
        var config = new RunnerConfig { CodeHost = _setup.Config.CodeHost };
        var options = new DashboardAppOptions(config, status, _time, _webRoot)
        {
            Commands = _commands,
            Setup = _setup.CreateSetup(_identity, _commands),
        };
        var builder = DashboardApp.CreateBuilder(options);
        builder.WebHost.UseTestServer();

        // Everything the web app logs, at every level, goes to the same list as the runner's lines.
        builder.Logging.AddProvider(_setup.Logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        _app = DashboardApp.Build(builder, options);
        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");

        return client;
    }

    private static HttpRequestMessage Write(HttpMethod method, string path, object? body = null)
    {
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WriteGuard.HeaderName, "1");

        return request;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpClient client,
        HttpRequestMessage request)
    {
        using (request)
        {
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            _answers.Add(text);
            var body = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();

            return (response.StatusCode, body);
        }
    }

    private async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        var (_, body) = await SendAsync(client, request);

        return body;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> SaveTokenAsync(HttpClient client, string token = Token) =>
        SendAsync(client, Write(HttpMethod.Put, "api/setup/token", new { token }));

    // ---- reads ----

    [Fact]
    public async Task Setup_mode_shows_in_the_setup_and_the_health()
    {
        var client = await StartAsync();

        var setup = await GetAsync(client, "api/setup");
        var health = await GetAsync(client, "api/health");

        Assert.False(setup.GetProperty("isTokenSet").GetBoolean());
        Assert.Equal("none", setup.GetProperty("tokenSource").GetString());
        Assert.Equal("Not set", setup.GetProperty("tokenSourceLabel").GetString());
        Assert.Equal(JsonValueKind.Null, setup.GetProperty("tokenNote").ValueKind);
        Assert.Equal(SetupFixture.BaseUrl, setup.GetProperty("baseUrl").GetString());
        Assert.Equal(_setup.Store.FilePath, setup.GetProperty("secretsFile").GetString());
        Assert.False(setup.GetProperty("canRemoveToken").GetBoolean());
        Assert.Equal("needs-setup", health.GetProperty("state").GetString());
        Assert.True(health.GetProperty("isSetupNeeded").GetBoolean());
    }

    [Fact]
    public async Task Each_source_comes_with_its_label_and_a_note_where_it_is_changed()
    {
        var environment = new CredentialEnvironment("env-token", null);
        var client = await StartAsync(new SetupFixture(environment, reposYmlWebPassword: "yml-pw"));

        var setup = await GetAsync(client, "api/setup");

        Assert.Equal("Environment variable", setup.GetProperty("tokenSourceLabel").GetString());
        Assert.Contains("GITBUCKET_TOKEN", setup.GetProperty("tokenNote").GetString());
        Assert.Equal("repos.yml", setup.GetProperty("webPasswordSourceLabel").GetString());
        Assert.Contains("code_host.web_password", setup.GetProperty("webPasswordNote").GetString());
    }

    // ---- token ----

    [Fact]
    public async Task An_accepted_token_answers_with_the_login_and_ends_setup_mode()
    {
        var client = await StartAsync();

        var (status, body) = await SaveTokenAsync(client);
        var health = await GetAsync(client, "api/health");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("alice", body.GetProperty("message").GetString());
        var setup = body.GetProperty("setup");
        Assert.Equal("alice", setup.GetProperty("login").GetString());
        Assert.Equal("user-secrets", setup.GetProperty("tokenSource").GetString());
        Assert.True(setup.GetProperty("canRemoveToken").GetBoolean());
        Assert.False(health.GetProperty("isSetupNeeded").GetBoolean());
        Assert.True(_commands.WaitForWakeAsync(default).IsCompleted);
    }

    [Fact]
    public async Task A_refused_token_is_400_with_its_reason()
    {
        _setup.CheckHost.CurrentUserError = new CodeHostException(HttpStatusCode.Unauthorized, "401");
        var client = await StartAsync();

        var (status, body) = await SaveTokenAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("The token was not accepted", body.GetProperty("message").GetString());
        Assert.False(File.Exists(_setup.Store.FilePath));
    }

    [Fact]
    public async Task An_unreachable_host_is_400_with_the_vpn_hint()
    {
        _setup.CheckHost.CurrentUserError = new HttpRequestException("No such host is known.");
        var client = await StartAsync();

        var (status, body) = await SaveTokenAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("is not reachable (VPN?)", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Deleting_a_token_from_the_environment_is_409()
    {
        var client = await StartAsync(new SetupFixture(new CredentialEnvironment("env-token", null)));

        var (status, body) = await SendAsync(client, Write(HttpMethod.Delete, "api/setup/token"));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("environment", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Deleting_a_saved_token_goes_back_to_setup_mode()
    {
        var client = await StartAsync();
        await SaveTokenAsync(client);

        var (status, body) = await SendAsync(client, Write(HttpMethod.Delete, "api/setup/token"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("setup").GetProperty("isTokenSet").GetBoolean());
    }

    // ---- web password ----

    [Fact]
    public async Task The_web_password_is_checked_with_a_sign_in_and_saved()
    {
        var client = await StartAsync();
        await SaveTokenAsync(client);

        var (wrongStatus, _) = await SendAsync(
            client,
            Write(HttpMethod.Put, "api/setup/web-password", new { password = "wrong" }));
        var (status, body) = await SendAsync(
            client,
            Write(HttpMethod.Put, "api/setup/web-password", new { password = WebPassword }));

        Assert.Equal(HttpStatusCode.BadRequest, wrongStatus);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("user-secrets", body.GetProperty("setup").GetProperty("webPasswordSource").GetString());
        Assert.True(body.GetProperty("setup").GetProperty("isWebPasswordSet").GetBoolean());
    }

    [Fact]
    public async Task Deleting_a_web_password_from_repos_yml_is_409()
    {
        var client = await StartAsync(new SetupFixture(reposYmlWebPassword: "yml-pw"));

        var (status, _) = await SendAsync(client, Write(HttpMethod.Delete, "api/setup/web-password"));

        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    // ---- secrets never leave the runner ----

    /// <summary>The ways a check can end, each with a server or exception text that quotes the secret.</summary>
    public static TheoryData<string> CheckEndings => ["accepted", "401", "unreachable", "502", "unexpected"];

    [Theory]
    [MemberData(nameof(CheckEndings))]
    public async Task No_answer_and_no_log_line_holds_a_secret(string ending)
    {
        var echo = $"server says: token {Token} / password {WebPassword}";
        var environment = new CredentialEnvironment("env-token-SECRET", null);
        var setup = new SetupFixture(environment, reposYmlWebPassword: "yml-pw-SECRET");
        setup.CheckHost.CurrentUserError = ending switch
        {
            "401" => new CodeHostException(HttpStatusCode.Unauthorized, echo),
            "unreachable" => new HttpRequestException(echo),
            "502" => new CodeHostException(HttpStatusCode.BadGateway, echo),
            "unexpected" => new InvalidOperationException(echo),
            _ => null,
        };
        var client = await StartAsync(setup);

        await GetAsync(client, "api/setup");
        await SaveTokenAsync(client);
        await SendAsync(client, Write(HttpMethod.Put, "api/setup/web-password", new { password = "wrong-pw-SECRET" }));
        await SendAsync(client, Write(HttpMethod.Put, "api/setup/web-password", new { password = WebPassword }));
        await GetAsync(client, "api/setup");
        await GetAsync(client, "api/health");
        await SendAsync(client, Write(HttpMethod.Delete, "api/setup/web-password"));
        await SendAsync(client, Write(HttpMethod.Delete, "api/setup/token"));
        await SendAsync(client, Write(HttpMethod.Delete, "api/setup/token"));
        await GetAsync(client, "api/setup");

        // The check really ran with the token and ended as asked, so a leak had its chance to happen.
        Assert.Contains(Token, _setup.CheckedTokens);
        var isAccepted = ending == "accepted";
        Assert.Equal(isAccepted, _setup.CheckedWebPasswords.Count > 0);

        string[] secrets = [Token, WebPassword, "env-token-SECRET", "yml-pw-SECRET", "wrong-pw-SECRET"];
        var logText = string.Join("\n", _setup.Logs.Lines.Select(line => line.Message));
        var answerText = string.Join("\n", _answers);
        Assert.NotEmpty(_setup.Logs.Lines);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, answerText);
            Assert.DoesNotContain(secret, logText);
        }
    }

    [Fact]
    public async Task A_setup_write_without_the_write_guard_header_is_403_and_saves_nothing()
    {
        var client = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, "api/setup/token")
        {
            Content = JsonContent.Create(new { token = Token }),
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_setup.CheckedTokens);
        Assert.False(File.Exists(_setup.Store.FilePath));
    }
}
