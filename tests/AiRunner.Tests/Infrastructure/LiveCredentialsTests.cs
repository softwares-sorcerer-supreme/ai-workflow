using System.Net;
using System.Text;
using AiRunner.Configuration;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Infrastructure;

/// <summary>
/// A secret saved on the Setup page is used from the next request on, without a restart: the API, the attachment
/// downloads, git and the web sign-in all read <see cref="RunnerCredentials"/> when they need it.
/// </summary>
public class LiveCredentialsTests
{
    private const string AttachmentUrl = "http://gb/o/r/_attached/a1";

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location, UriKind.Relative) },
    };

    private static string? TokenOf(HttpRequestMessage request) =>
        request.Headers.Authorization is { } authorization ? $"{authorization.Scheme} {authorization.Parameter}" : null;

    private static (GitBucketCodeHost CodeHost, StubHttpHandler Handler) CodeHostWith(RunnerCredentials credentials)
    {
        var handler = new StubHttpHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? Json("""{"login":"bot"}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var apiClient = new HttpClient(handler) { BaseAddress = new Uri("http://gb/api/v3/") };
        var web = new GitBucketWeb(new HttpClient(handler) { BaseAddress = new Uri("http://gb/") }, credentials);
        var codeHost = new GitBucketCodeHost(apiClient, new HttpClient(handler), web, credentials, "http://gb");

        return (codeHost, handler);
    }

    [Fact]
    public async Task The_next_api_request_carries_a_new_token()
    {
        var credentials = RunnerCredentials.Of(token: "old-token");
        var (codeHost, handler) = CodeHostWith(credentials);

        await codeHost.GetCurrentUserAsync(default);
        credentials.Apply(RunnerCredentials.Of(token: "new-token").Current);
        await codeHost.GetCurrentUserAsync(default);

        Assert.Equal(["token old-token", "token new-token"], handler.Requests.Select(TokenOf));
    }

    [Fact]
    public async Task The_next_attachment_download_carries_a_new_token()
    {
        var folder = Directory.CreateTempSubdirectory("ai-runner-live-").FullName;
        try
        {
            var credentials = RunnerCredentials.Of(token: "old-token");
            var (codeHost, handler) = CodeHostWith(credentials);
            var download = new AttachmentDownload(AttachmentUrl, folder, "a1", 1000);

            await codeHost.DownloadAttachmentAsync(download, default);
            credentials.Apply(RunnerCredentials.Of(token: "new-token").Current);
            await codeHost.DownloadAttachmentAsync(download with { FileStem = "a2" }, default);

            Assert.Equal(["token old-token", "token new-token"], handler.Requests.Select(TokenOf));
        }
        finally
        {
            FileSystem.DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task Without_a_token_no_authorization_header_is_sent()
    {
        var (codeHost, handler) = CodeHostWith(new RunnerCredentials(CredentialValues.Empty));

        await codeHost.GetCurrentUserAsync(default);

        Assert.Null(TokenOf(Assert.Single(handler.Requests)));
    }

    [Fact]
    public async Task An_error_message_never_quotes_the_token_a_server_echoes()
    {
        var credentials = RunnerCredentials.Of(token: "echoed-token");
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("bad header: token echoed-token"),
        });
        var apiClient = new HttpClient(handler) { BaseAddress = new Uri("http://gb/api/v3/") };
        var web = new GitBucketWeb(new HttpClient(handler), credentials);
        var codeHost = new GitBucketCodeHost(apiClient, new HttpClient(handler), web, credentials, "http://gb");

        var exception = await Assert.ThrowsAsync<CodeHostException>(() => codeHost.GetCurrentUserAsync(default));

        Assert.DoesNotContain("echoed-token", exception.Message);
        Assert.Contains("***", exception.Message);
    }

    [Fact]
    public void Git_reads_the_secret_per_call()
    {
        var config = RunnerConfig.Parse("""
            code_host: { kind: gitbucket, base_url: "http://gb.test/" }
            """);
        var credentials = RunnerCredentials.Of(token: "old-token");
        var remote = new GitBucketGitRemote(
            RunnerConfigSource.Fixed(config),
            new RunnerIdentity { Login = "bot" },
            credentials);

        var before = remote.AuthorizationHeader();
        credentials.Apply(RunnerCredentials.Of(token: "new-token").Current);
        var after = remote.AuthorizationHeader();

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("bot:new-token"));
        Assert.NotEqual(before, after);
        Assert.Equal($"Authorization: Basic {expected}", after);
    }

    [Fact]
    public async Task The_web_side_signs_in_again_after_a_new_web_password()
    {
        var signIns = new List<string>();
        var handler = new StubHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/signin")
            {
                var form = request.Content!.ReadAsStringAsync().Result;
                signIns.Add(form);
            }

            var location = path == "/signin" ? "/" : "/o/r/issues/7";

            return Redirect(location);
        });
        var credentials = RunnerCredentials.Of(token: "t", webPassword: "old-pw");
        var web = new GitBucketWeb(new HttpClient(handler) { BaseAddress = new Uri("http://gb/") }, credentials);
        KeyValuePair<string, string>[] form = [new("action", "close")];

        await web.PostSignedInAsync("o/r/issue_comments/state", form, _ => Task.FromResult("bot"), default);
        await web.PostSignedInAsync("o/r/issue_comments/state", form, _ => Task.FromResult("bot"), default);
        credentials.Apply(RunnerCredentials.Of(token: "t", webPassword: "new-pw").Current);
        await web.PostSignedInAsync("o/r/issue_comments/state", form, _ => Task.FromResult("bot"), default);

        Assert.Equal(2, signIns.Count);
        Assert.Contains("password=old-pw", signIns[0]);
        Assert.Contains("password=new-pw", signIns[1]);
    }

    // ---- the Setup page's checks ----

    [Fact]
    public async Task A_token_check_tells_accepted_refused_and_unreachable_apart()
    {
        var accepted = new FakeCodeHost { Login = "alice" };
        var refused = new FakeCodeHost { CurrentUserError = new CodeHostException(HttpStatusCode.Unauthorized, "401") };
        var forbidden = new FakeCodeHost { CurrentUserError = new CodeHostException(HttpStatusCode.Forbidden, "403") };
        var unreachable = new FakeCodeHost { CurrentUserError = new HttpRequestException("No such host is known.") };
        var timedOut = new FakeCodeHost { CurrentUserError = new TaskCanceledException("timeout") };
        var broken = new FakeCodeHost { CurrentUserError = new CodeHostException(HttpStatusCode.BadGateway, "502") };
        var hosts = new Dictionary<string, FakeCodeHost>
        {
            ["ok"] = accepted,
            ["401"] = refused,
            ["403"] = forbidden,
            ["down"] = unreachable,
            ["slow"] = timedOut,
            ["502"] = broken,
        };
        var check = new GitBucketCredentialCheck(token => hosts[token], _ => throw new InvalidOperationException());

        var answers = new Dictionary<string, CredentialCheck>();
        foreach (var token in hosts.Keys)
        {
            answers[token] = await check.CheckTokenAsync(token, default);
        }

        Assert.Equal(new CredentialCheck(CheckVerdict.Accepted, "alice"), answers["ok"]);
        Assert.Equal(CheckVerdict.NotAccepted, answers["401"].Verdict);
        Assert.Equal(CheckVerdict.NotAccepted, answers["403"].Verdict);
        Assert.Equal(CheckVerdict.Unreachable, answers["down"].Verdict);
        Assert.Equal(CheckVerdict.Unreachable, answers["slow"].Verdict);
        Assert.Equal(new CredentialCheck(CheckVerdict.Failed, Status: 502), answers["502"]);
    }

    [Fact]
    public async Task A_web_password_check_signs_in_on_its_own_client()
    {
        var handler = new StubHttpHandler(request =>
        {
            var form = request.Content!.ReadAsStringAsync().Result;
            var location = form.Contains("password=right", StringComparison.Ordinal) ? "/" : "/signin";

            return Redirect(location);
        });
        var check = new GitBucketCredentialCheck(
            _ => throw new InvalidOperationException(),
            password => new GitBucketWeb(
                new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://gb/") },
                RunnerCredentials.Of(webPassword: password)));

        var right = await check.CheckWebPasswordAsync("bot", "right", default);
        var wrong = await check.CheckWebPasswordAsync("bot", "wrong", default);

        Assert.True(right.IsAccepted);
        Assert.Equal(CheckVerdict.NotAccepted, wrong.Verdict);
        Assert.All(handler.Urls, url => Assert.Equal("/signin", url));
    }
}
