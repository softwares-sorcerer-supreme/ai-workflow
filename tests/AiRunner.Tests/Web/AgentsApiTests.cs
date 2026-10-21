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
/// The agents routes on a TestServer with the real <see cref="RunnerAgents"/> over a temp folder and a temp Copilot
/// home (the path comes from a request, so the boundary is tested through real URLs).
/// </summary>
public sealed class AgentsApiTests : IAsyncDisposable
{
    private const string Agent = "---\nname: scout\ndescription: Finds code.\n---\n\nBody\n";

    private readonly FakeTime _time = new();
    private readonly string _root = Directory.CreateTempSubdirectory("ai-runner-agents-api-").FullName;
    private readonly RunnerStatus _status;
    private readonly RunnerConfig _config;
    private WebApplication? _app;

    public AgentsApiTests()
    {
        _status = new RunnerStatus("laptop-1", "repos.yml", _time);
        _config = new RunnerConfig { RootDir = Path.Combine(_root, "repo") };
        Directory.CreateDirectory(Path.Combine(_root, "repo", "agents"));
        File.WriteAllText(Path.Combine(_root, "repo", "agents", "scout.agent.md"), Agent);
        Directory.CreateDirectory(Path.Combine(_root, "repo", "templates"));
        File.WriteAllText(Path.Combine(_root, "repo", "templates", "spec.md"), "# Spec\n");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        FileSystem.DeleteDirectory(_root);
    }

    private async Task<HttpClient> StartAsync()
    {
        var home = new CopilotHome(Path.Combine(_root, "copilot-home"));
        var files = new AgentFiles(_config, home, new ProcessRunner());
        var commands = new RunnerCommands();
        var installer = new AgentInstaller(_config, NullLogger<AgentInstaller>.Instance);
        var sync = new AgentSync(installer, home, commands, NullLogger<AgentSync>.Instance);
        var options = new DashboardAppOptions(_config, _status, _time, Path.Combine(_root, "wwwroot"))
        {
            Agents = new RunnerAgents(files, sync, NullLogger<RunnerAgents>.Instance),
            Commands = commands,
        };
        var builder = DashboardApp.CreateBuilder(options);
        builder.WebHost.UseTestServer();
        _app = DashboardApp.Build(builder, options);
        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");
        client.DefaultRequestHeaders.Add(WriteGuard.HeaderName, "1");

        return client;
    }

    /// <summary>The file's text, or null while it is missing or still being written by the copy.</summary>
    private static string? TextOrNull(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task The_list_shows_files_with_labels_the_sync_banner_flag_and_the_templates_note()
    {
        var client = await StartAsync();

        using var response = await client.GetAsync("api/agents");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.True(body.GetProperty("isOutOfSync").GetBoolean());
        Assert.Contains("code change", body.GetProperty("templatesNote").GetString());
        var agent = body.GetProperty("files").EnumerateArray().Single(file => file.GetProperty("kind").GetString() == "agent");
        Assert.Equal("agents/scout.agent.md", agent.GetProperty("path").GetString());
        Assert.Equal("missing", agent.GetProperty("syncState").GetString());
        Assert.Equal("Not installed", agent.GetProperty("syncStateLabel").GetString());
        Assert.Equal("unknown", agent.GetProperty("gitState").GetString());
        var template = body.GetProperty("files").EnumerateArray().Single(file => file.GetProperty("kind").GetString() == "template");
        Assert.False(template.GetProperty("isEditable").GetBoolean());
    }

    [Fact]
    public async Task One_file_comes_with_text_and_hash()
    {
        var client = await StartAsync();

        using var response = await client.GetAsync("api/agents/file?path=agents/scout.agent.md");

        var body = await BodyOf(response);
        Assert.Equal(Agent, body.GetProperty("text").GetString());
        Assert.NotEmpty(body.GetProperty("hash").GetString()!);
    }

    [Theory]
    [InlineData("agents/missing.agent.md")]
    [InlineData("../repos.yml")]
    [InlineData("agents/../../x")]
    [InlineData("src/Program.cs")]
    public async Task A_path_that_is_not_a_shared_file_is_404(string path)
    {
        var client = await StartAsync();

        using var response = await client.GetAsync($"api/agents/file?path={Uri.EscapeDataString(path)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Save_answers_202_and_the_file_changes()
    {
        var client = await StartAsync();
        using var read = await client.GetAsync("api/agents/file?path=agents/scout.agent.md");
        var hash = (await BodyOf(read)).GetProperty("hash").GetString();
        var text = Agent.Replace("Body", "New body");

        using var response = await client.PutAsync(
            "api/agents/file?path=agents/scout.agent.md",
            Json(new { text, baseHash = hash }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(text, File.ReadAllText(Path.Combine(_root, "repo", "agents", "scout.agent.md")));
    }

    [Fact]
    public async Task Save_with_a_stale_hash_is_409()
    {
        var client = await StartAsync();

        using var response = await client.PutAsync(
            "api/agents/file?path=agents/scout.agent.md",
            Json(new { text = Agent, baseHash = "stale" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Save_that_breaks_the_rules_is_400_with_every_reason()
    {
        var client = await StartAsync();
        using var read = await client.GetAsync("api/agents/file?path=agents/scout.agent.md");
        var hash = (await BodyOf(read)).GetProperty("hash").GetString();

        using var response = await client.PutAsync(
            "api/agents/file?path=agents/scout.agent.md",
            Json(new { text = "---\nname: other\n---\n", baseHash = hash }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var reasons = (await BodyOf(response)).GetProperty("reasons").EnumerateArray().ToList();
        Assert.Equal(2, reasons.Count);
        Assert.Equal(Agent, File.ReadAllText(Path.Combine(_root, "repo", "agents", "scout.agent.md")));
    }

    [Fact]
    public async Task A_template_is_readable_but_a_save_is_400()
    {
        var client = await StartAsync();
        using var read = await client.GetAsync("api/agents/file?path=templates/spec.md");
        var hash = (await BodyOf(read)).GetProperty("hash").GetString();

        using var response = await client.PutAsync(
            "api/agents/file?path=templates/spec.md",
            Json(new { text = "x", baseHash = hash }));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("# Spec\n", File.ReadAllText(Path.Combine(_root, "repo", "templates", "spec.md")));
    }

    [Fact]
    public async Task Save_does_not_create_a_file()
    {
        var client = await StartAsync();

        using var response = await client.PutAsync(
            "api/agents/file?path=agents/new.agent.md",
            Json(new { text = Agent.Replace("scout", "new"), baseHash = "x" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, "repo", "agents", "new.agent.md")));
    }

    [Fact]
    public async Task Revert_of_a_file_outside_git_is_400()
    {
        var client = await StartAsync();

        using var response = await client.PostAsync("api/agents/revert?path=agents/scout.agent.md", Json(new { }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sync_answers_202_and_copies_to_the_home_folder()
    {
        var client = await StartAsync();

        using var response = await client.PostAsync("api/agents/sync", Json(new { }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var copy = Path.Combine(_root, "copilot-home", "agents", "scout.agent.md");
        await Wait.UntilAsync(() => TextOrNull(copy) == Agent);
    }

    [Fact]
    public async Task Health_says_when_the_agents_have_drifted()
    {
        var client = await StartAsync();

        var before = await BodyOf(await client.GetAsync("api/health"));
        _status.AgentDriftChecked(true);
        var after = await BodyOf(await client.GetAsync("api/health"));

        Assert.False(before.GetProperty("hasAgentDrift").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("agentDriftLabel").ValueKind);
        Assert.True(after.GetProperty("hasAgentDrift").GetBoolean());
        Assert.Equal("Agents out of sync", after.GetProperty("agentDriftLabel").GetString());
    }
}
