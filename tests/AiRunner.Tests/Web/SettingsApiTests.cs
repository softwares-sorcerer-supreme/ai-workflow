using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using AiRunner.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Web;

/// <summary>
/// The Settings screen's routes on a TestServer over a real <see cref="RunnerSettings"/> and a temp repos.yml: the
/// schema, the file without its secret, a save (applied at once), and the stale, invalid and guarded cases.
/// </summary>
public sealed class SettingsApiTests : IAsyncDisposable
{
    private const string WebPassword = "pw-SECRET-0815";

    private static readonly string Start = $$"""
        code_host: { kind: gitbucket, base_url: http://host, web_password: {{WebPassword}} }
        poll_interval_seconds: 60
        projects:
          p:
            components: { c: { repo: o/r } }
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("settings-api-").FullName;
    private readonly FakeTime _time = new();
    private readonly RunnerCommands _commands = new();
    private RunnerConfigSource _source = null!;
    private WebApplication? _app;

    /// <summary>Every answer body the test read, for the leak check.</summary>
    private readonly List<string> _answers = [];

    private string ConfigPath => Path.Combine(_folder, "repos.yml");

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
        File.WriteAllText(ConfigPath, Start);
        _source = RunnerConfigSource.Load(ConfigPath);
        var store = new ConfigFileStore(_time);
        var settings = new RunnerSettings(_source, store, _commands, NullLogger<RunnerSettings>.Instance);
        var status = new RunnerStatus("laptop-1", ConfigPath, _time);
        var webRoot = Path.Combine(_folder, "wwwroot");
        var options = new DashboardAppOptions(_source, status, _time, webRoot)
        {
            Commands = _commands,
            Settings = settings,
        };
        var builder = DashboardApp.CreateBuilder(options);
        builder.WebHost.UseTestServer();

        _app = DashboardApp.Build(builder, options);
        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");

        return client;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            _answers.Add(text);
            var body = text.Length == 0 ? null : JsonNode.Parse(text);

            return (response.StatusCode, body);
        }
    }

    private async Task<JsonNode> GetAsync(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        var (_, body) = await SendAsync(client, request);

        return body!;
    }

    private static HttpRequestMessage Put(JsonNode file, string baseHash, bool hasGuardHeader = true)
    {
        var body = new JsonObject { ["file"] = file.DeepClone(), ["baseHash"] = baseHash };
        var request = new HttpRequestMessage(HttpMethod.Put, "api/settings")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (hasGuardHeader)
        {
            request.Headers.Add(WriteGuard.HeaderName, "1");
        }

        return request;
    }

    [Fact]
    public async Task The_schema_lists_the_keys_with_their_help()
    {
        var client = await StartAsync();

        var schema = await GetAsync(client, "api/settings/schema");

        var entries = schema["settings"]!.AsArray();
        var port = entries.Single(entry => entry!["path"]!.GetValue<string>() == "dashboard.port")!;
        Assert.True(port["isRestartNeeded"]!.GetValue<bool>());
        Assert.Equal(5180, port["default"]!.GetValue<int>());
        Assert.Equal("global", port["scope"]!.GetValue<string>());
        Assert.Contains(entries, entry => entry!["path"]!.GetValue<string>() == "projects.*.components.*.commands.build");
    }

    [Fact]
    public async Task The_settings_hold_the_file_its_hash_and_the_effective_values_but_no_secret()
    {
        var client = await StartAsync();

        var settings = await GetAsync(client, "api/settings");

        Assert.Equal(RunnerConfigSource.HashOf(Start), settings["hash"]!.GetValue<string>());
        Assert.Equal(60, settings["file"]!["poll_interval_seconds"]!.GetValue<int>());
        Assert.True(settings["file"]!["code_host"]!["web_password"]!["isSet"]!.GetValue<bool>());
        var effective = settings["effective"]!.AsArray().Single()!;
        Assert.Equal("p", effective["project"]!.GetValue<string>());
        Assert.Contains(
            effective["values"]!.AsArray(),
            value => value!["key"]!.GetValue<string>() == "limits.max_reviews" &&
                     value["source"]!.GetValue<string>() == "default");
        Assert.All(_answers, answer => Assert.DoesNotContain(WebPassword, answer));
    }

    [Fact]
    public async Task A_save_is_applied_and_answers_the_new_settings()
    {
        var client = await StartAsync();
        var settings = await GetAsync(client, "api/settings");
        var file = settings["file"]!;
        file["poll_interval_seconds"] = 20;

        var (status, body) = await SendAsync(client, Put(file, settings["hash"]!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Saved and applied.", body!["message"]!.GetValue<string>());
        Assert.Equal(20, body["settings"]!["file"]!["poll_interval_seconds"]!.GetValue<int>());
        Assert.Equal(20, _source.Current.PollIntervalSeconds);
        Assert.Contains(WebPassword, File.ReadAllText(ConfigPath));
        Assert.All(_answers, answer => Assert.DoesNotContain(WebPassword, answer));
    }

    [Fact]
    public async Task A_save_from_an_old_read_is_a_conflict()
    {
        var client = await StartAsync();
        var settings = await GetAsync(client, "api/settings");
        File.WriteAllText(ConfigPath, Start.Replace("60", "61"));

        var (status, body) = await SendAsync(client, Put(settings["file"]!, settings["hash"]!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("changed since", body!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_invalid_save_is_a_bad_request_with_the_resolver_message()
    {
        var client = await StartAsync();
        var settings = await GetAsync(client, "api/settings");
        var file = settings["file"]!;
        file["projects"]!["p"]!["review"] = new JsonObject { ["scope"] = "nobody" };

        var (status, body) = await SendAsync(client, Put(file, settings["hash"]!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("review.scope 'nobody' is unknown", body!["message"]!.GetValue<string>());
        Assert.Equal(Start, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task A_save_without_the_guard_header_is_refused()
    {
        var client = await StartAsync();
        var settings = await GetAsync(client, "api/settings");

        var (status, _) = await SendAsync(
            client,
            Put(settings["file"]!, settings["hash"]!.GetValue<string>(), hasGuardHeader: false));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(Start, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task A_broken_hand_edit_shows_in_the_health_and_the_settings()
    {
        var client = await StartAsync();
        File.WriteAllText(ConfigPath, Start + "\nmystery_key: 1\n");

        var settings = await GetAsync(client, "api/settings");
        var health = await GetAsync(client, "api/health");

        Assert.Contains("mystery_key", settings["error"]!.GetValue<string>());
        Assert.Contains("mystery_key", health["configError"]!.GetValue<string>());
        Assert.Equal("repos.yml has errors", health["configErrorLabel"]!.GetValue<string>());
    }
}
