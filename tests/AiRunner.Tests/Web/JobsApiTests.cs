using System.Net;
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
/// Overview, job history and the log viewer on a TestServer over a temp SQLite file and a temp logs folder.
/// The log viewer is a security boundary: the last tests try to read outside the job's folder.
/// </summary>
public sealed class JobsApiTests : IAsyncDisposable
{
    private const string Secret = "TOP-SECRET-OUTSIDE-THE-JOB-FOLDER";

    private readonly FakeTime _time = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-jobs-api-").FullName;
    private readonly RunnerStatus _status;
    private readonly RunnerConfig _config;
    private readonly SqliteStateStore _store;
    private WebApplication? _app;

    public JobsApiTests()
    {
        _status = new RunnerStatus("laptop-1", "repos.yml", _time);
        _config = new RunnerConfig
        {
            Paths = new PathsConfig { Logs = Path.Combine(_folder, "logs") },
            Projects =
            [
                new ProjectConfig
                {
                    Name = "shop",
                    IssuesIn = "api",
                    Components = [new ComponentConfig { Name = "api", Repo = "o/shop", Code = true }],
                },
                new ProjectConfig
                {
                    Name = "blog",
                    IssuesIn = "web",
                    Components = [new ComponentConfig { Name = "web", Repo = "o/blog", Code = true }],
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

    private string JobsRoot => Path.Combine(_folder, "logs", "jobs");

    private async Task<HttpClient> StartAsync()
    {
        var options = new DashboardAppOptions(_config, _status, _time, Path.Combine(_folder, "wwwroot"))
        {
            Jobs = _store,
        };
        var builder = DashboardApp.CreateBuilder(options);
        builder.WebHost.UseTestServer();
        _app = DashboardApp.Build(builder, options);
        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost/");

        return client;
    }

    private long AddJob(JobType type, string repo, int number, string status = JobStatus.Done, string? logFolder = null)
    {
        var id = _store.AddJob(new JobRequest(type, repo, number));
        if (status != JobStatus.Pending)
        {
            _store.SetJobStatus(id, JobStatus.Running);
        }

        if (status is JobStatus.Done or JobStatus.Failed)
        {
            _store.SetJobStatus(id, status, status == JobStatus.Failed ? "boom" : null);
        }

        if (logFolder is not null)
        {
            _store.SetJobLogDir(id, logFolder);
        }

        return id;
    }

    private string MakeLogFolder(string name, params (string File, string Content)[] files)
    {
        var folder = Path.Combine(JobsRoot, name);
        Directory.CreateDirectory(folder);
        foreach (var (file, content) in files)
        {
            File.WriteAllText(Path.Combine(folder, file), content);
        }

        return name;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var json = await client.GetStringAsync(url);

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // ---- overview ----

    [Fact]
    public async Task Overview_of_an_idle_runner_has_no_running_job_and_an_empty_queue()
    {
        var client = await StartAsync();

        var root = await GetJsonAsync(client, "api/overview");

        Assert.Equal(JsonValueKind.Null, root.GetProperty("runningJob").ValueKind);
        Assert.Equal("Queue is empty", root.GetProperty("queueLabel").GetString());
        Assert.Empty(root.GetProperty("queue").EnumerateArray());
    }

    [Fact]
    public async Task Overview_shows_the_running_job_with_its_round_and_the_queue_in_order()
    {
        var startedAt = _time.GetUtcNow();
        _status.WorkerBusy(new RunningJob(5, "ImplementIssue", "o/shop", 12, startedAt));
        _status.JobRound(2);
        _time.Advance(TimeSpan.FromSeconds(90));
        var first = AddJob(JobType.AnalyzeIssue, "o/blog", 3, JobStatus.Pending);
        var second = AddJob(JobType.ReviewPullRequest, "o/shop", 9, JobStatus.Pending);
        AddJob(JobType.FinishIssue, "o/shop", 1);
        var client = await StartAsync();

        var root = await GetJsonAsync(client, "api/overview");

        var running = root.GetProperty("runningJob");
        Assert.Equal("ImplementIssue", running.GetProperty("type").GetString());
        Assert.Equal("Implement issue", running.GetProperty("typeLabel").GetString());
        Assert.Equal("o/shop", running.GetProperty("repo").GetString());
        Assert.Equal(12, running.GetProperty("number").GetInt32());
        Assert.Equal("shop", running.GetProperty("project").GetString());
        Assert.Equal(2, running.GetProperty("round").GetInt32());
        Assert.Equal("Round 2", running.GetProperty("roundLabel").GetString());
        Assert.Equal(90, running.GetProperty("elapsedSeconds").GetDouble());
        Assert.Equal("2 jobs queued", root.GetProperty("queueLabel").GetString());
        var queueIds = root.GetProperty("queue").EnumerateArray().Select(job => job.GetProperty("id").GetInt64());
        Assert.Equal([first, second], queueIds);
    }

    // ---- jobs ----

    [Fact]
    public async Task Jobs_are_listed_newest_first_with_codes_labels_and_project()
    {
        AddJob(JobType.AnalyzeIssue, "o/blog", 1);
        var failed = AddJob(JobType.ImplementIssue, "o/shop", 2, JobStatus.Failed, MakeLogFolder("run-2"));
        var client = await StartAsync();

        var root = await GetJsonAsync(client, "api/jobs");

        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(1, root.GetProperty("totalPages").GetInt32());
        var first = root.GetProperty("items")[0];
        Assert.Equal(failed, first.GetProperty("id").GetInt64());
        Assert.Equal("failed", first.GetProperty("status").GetString());
        Assert.Equal("Failed", first.GetProperty("statusLabel").GetString());
        Assert.Equal("shop", first.GetProperty("project").GetString());
        Assert.Equal("boom", first.GetProperty("error").GetString());
        Assert.True(first.GetProperty("hasLog").GetBoolean());
        Assert.False(root.GetProperty("items")[1].GetProperty("hasLog").GetBoolean());
    }

    [Fact]
    public async Task Jobs_filter_by_status_type_and_project_and_page()
    {
        AddJob(JobType.AnalyzeIssue, "o/blog", 1);
        AddJob(JobType.AnalyzeIssue, "o/shop", 2, JobStatus.Failed);
        AddJob(JobType.ImplementIssue, "o/shop", 3, JobStatus.Failed);
        var client = await StartAsync();

        var byStatus = await GetJsonAsync(client, "api/jobs?status=failed");
        var byType = await GetJsonAsync(client, "api/jobs?type=AnalyzeIssue");
        var byProject = await GetJsonAsync(client, "api/jobs?project=blog");
        var pageTwo = await GetJsonAsync(client, "api/jobs?page=2");

        Assert.Equal(2, byStatus.GetProperty("total").GetInt32());
        Assert.Equal(2, byType.GetProperty("total").GetInt32());
        Assert.Equal(1, byProject.GetProperty("total").GetInt32());
        Assert.Equal(2, pageTwo.GetProperty("page").GetInt32());
        Assert.Empty(pageTwo.GetProperty("items").EnumerateArray());
        Assert.Equal(3, pageTwo.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("api/jobs?status=nope")]
    [InlineData("api/jobs?type=nope")]
    [InlineData("api/jobs?project=nope")]
    public async Task Jobs_with_an_unknown_filter_value_are_a_400_with_a_message(string url)
    {
        var client = await StartAsync();

        using var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task One_job_carries_its_result_and_an_unknown_job_is_404()
    {
        var id = AddJob(JobType.ImplementIssue, "o/shop", 4, JobStatus.Failed);
        _store.SetJobResult(id, "failed", "checks red");
        var client = await StartAsync();

        var root = await GetJsonAsync(client, $"api/jobs/{id}");
        using var missing = await client.GetAsync("api/jobs/9999");

        Assert.Equal("failed", root.GetProperty("result").GetString());
        Assert.Equal("checks red", root.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("POST", "api/jobs")]
    [InlineData("DELETE", "api/jobs/1")]
    [InlineData("PUT", "api/jobs/1/files/job.log")]
    [InlineData("POST", "api/overview")]
    public async Task The_new_routes_refuse_anything_but_get(string method, string path)
    {
        var client = await StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ---- log viewer ----

    [Fact]
    public async Task The_files_of_a_job_are_listed_and_one_is_returned_as_text_with_its_format()
    {
        var folder = MakeLogFolder("run-1", ("job.log", "10:00:00 started\n"), ("round-1-transcript.md", "# Transcript"));
        var id = AddJob(JobType.AnalyzeIssue, "o/shop", 1, JobStatus.Done, folder);
        var client = await StartAsync();

        var files = await GetJsonAsync(client, $"api/jobs/{id}/files");
        var log = await GetJsonAsync(client, $"api/jobs/{id}/files/job.log");
        var markdown = await GetJsonAsync(client, $"api/jobs/{id}/files/round-1-transcript.md");

        var names = files.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("name").GetString());
        Assert.Equal(["job.log", "round-1-transcript.md"], names);
        Assert.Equal("10:00:00 started\n", log.GetProperty("text").GetString());
        Assert.Equal("text", log.GetProperty("format").GetString());
        Assert.False(log.GetProperty("isTruncated").GetBoolean());
        Assert.Equal("markdown", markdown.GetProperty("format").GetString());
    }

    [Fact]
    public async Task A_job_without_a_log_folder_lists_no_files_and_an_unknown_file_is_404()
    {
        var withoutLog = AddJob(JobType.AnalyzeIssue, "o/shop", 1);
        var withLog = AddJob(JobType.AnalyzeIssue, "o/shop", 2, JobStatus.Done, MakeLogFolder("run-2", ("job.log", "x")));
        var client = await StartAsync();

        var files = await GetJsonAsync(client, $"api/jobs/{withoutLog}/files");
        using var noFolder = await client.GetAsync($"api/jobs/{withoutLog}/files/job.log");
        using var unknown = await client.GetAsync($"api/jobs/{withLog}/files/nothing.txt");
        using var unknownJob = await client.GetAsync("api/jobs/9999/files");

        Assert.Empty(files.GetProperty("files").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, noFolder.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownJob.StatusCode);
    }

    [Fact]
    public async Task A_file_over_2_MB_is_cut_with_a_note()
    {
        var folder = MakeLogFolder("run-big");
        var bigFile = Path.Combine(JobsRoot, folder, "big.log");
        File.WriteAllText(bigFile, new string('a', LogFileRules.MaxBytes + 1000));
        var id = AddJob(JobType.AnalyzeIssue, "o/shop", 1, JobStatus.Done, folder);
        var client = await StartAsync();

        var root = await GetJsonAsync(client, $"api/jobs/{id}/files/big.log");

        Assert.True(root.GetProperty("isTruncated").GetBoolean());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("truncatedNote").GetString()));
        Assert.Equal(LogFileRules.MaxBytes + 1000, root.GetProperty("sizeBytes").GetInt64());
        var text = root.GetProperty("text").GetString()!;
        Assert.InRange(text.Length, LogFileRules.MaxBytes, LogFileRules.MaxBytes + 100);
        Assert.StartsWith("aaaa", text);
    }

    [Theory]
    [InlineData("..%5Cstate.db")]
    [InlineData("..%2Fstate.db")]
    [InlineData("%2E%2E%5C%2E%2E%5Cstate.db")]
    [InlineData("%2Fetc%2Fpasswd")]
    [InlineData("C%3A%5Cwindows%5Cwin.ini")]
    [InlineData("%5Cwindows%5Cwin.ini")]
    [InlineData("secret.txt%3Astream")]
    public async Task The_log_viewer_never_serves_a_name_that_is_not_a_plain_file_in_the_folder(string name)
    {
        Directory.CreateDirectory(JobsRoot);
        File.WriteAllText(Path.Combine(JobsRoot, "secret.txt"), Secret);
        var folder = MakeLogFolder("run-1", ("job.log", "ok"));
        var id = AddJob(JobType.AnalyzeIssue, "o/shop", 1, JobStatus.Done, folder);
        var client = await StartAsync();

        using var response = await client.GetAsync($"api/jobs/{id}/files/{name}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Secret, body);
        Assert.DoesNotContain("SQLite", body);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("..\\other")]
    [InlineData("sub/run")]
    [InlineData("C:\\Windows")]
    [InlineData("/etc")]
    [InlineData("")]
    public void A_tampered_log_dir_in_the_database_cannot_point_the_viewer_elsewhere(string logDir)
    {
        var outside = Path.Combine(_folder, "logs", "other");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), Secret);
        File.WriteAllText(Path.Combine(_folder, "logs", "secret.txt"), Secret);
        Directory.CreateDirectory(JobsRoot);
        File.WriteAllText(Path.Combine(JobsRoot, "secret.txt"), Secret);
        var viewer = new LogViewer(_config);
        var job = new JobRecord(
            1, "AnalyzeIssue", "o/shop", 1, JobStatus.Done, 1, _time.GetUtcNow(), null, null, null, logDir, null, null);

        var files = viewer.List(job);
        var read = viewer.Read(job, "secret.txt");

        Assert.Empty(files);
        Assert.NotEqual(LogReadStatus.Ok, read.Status);
        Assert.Null(read.Content);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("sub/job.log")]
    [InlineData("sub\\job.log")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\win.ini")]
    [InlineData("C:\\windows\\win.ini")]
    [InlineData("")]
    public void The_viewer_refuses_unsafe_names_before_touching_the_disk(string name)
    {
        var folder = MakeLogFolder("run-1", ("job.log", "ok"));
        var viewer = new LogViewer(_config);
        var job = new JobRecord(
            1, "AnalyzeIssue", "o/shop", 1, JobStatus.Done, 1, _time.GetUtcNow(), null, null, null, folder, null, null);

        var read = viewer.Read(job, name);

        Assert.Equal(LogReadStatus.BadName, read.Status);
        Assert.Null(read.Content);
    }

    [Fact]
    public void The_viewer_does_not_follow_a_link_that_points_outside_the_job_folder()
    {
        var outsideFile = Path.Combine(_folder, "outside.txt");
        File.WriteAllText(outsideFile, Secret);
        var folder = MakeLogFolder("run-1", ("job.log", "ok"));
        var link = Path.Combine(JobsRoot, folder, "link.txt");

        try
        {
            File.CreateSymbolicLink(link, outsideFile);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // Creating links needs a privilege on Windows; without it there is nothing to follow.
            return;
        }

        var viewer = new LogViewer(_config);
        var job = new JobRecord(
            1, "AnalyzeIssue", "o/shop", 1, JobStatus.Done, 1, _time.GetUtcNow(), null, null, null, folder, null, null);

        var files = viewer.List(job);
        var read = viewer.Read(job, "link.txt");

        Assert.DoesNotContain(files, file => file.Name == "link.txt");
        Assert.Equal(LogReadStatus.NotFound, read.Status);
    }

    [Fact]
    public void A_sub_folder_of_the_job_folder_is_not_a_file()
    {
        var folder = MakeLogFolder("run-1", ("job.log", "ok"));
        var subFolder = Path.Combine(JobsRoot, folder, "nested");
        Directory.CreateDirectory(subFolder);
        File.WriteAllText(Path.Combine(subFolder, "deep.txt"), Secret);
        var viewer = new LogViewer(_config);
        var job = new JobRecord(
            1, "AnalyzeIssue", "o/shop", 1, JobStatus.Done, 1, _time.GetUtcNow(), null, null, null, folder, null, null);

        var files = viewer.List(job);
        var readFolder = viewer.Read(job, "nested");

        Assert.Equal(["job.log"], files.Select(file => file.Name));
        Assert.Equal(LogReadStatus.NotFound, readFolder.Status);
    }
}
