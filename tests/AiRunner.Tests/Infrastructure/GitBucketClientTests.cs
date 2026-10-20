using System.Net;
using System.Text;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Infrastructure;

/// <summary>GitBucketClient against canned JSON: paging, field mapping, what is sent.</summary>
public class GitBucketClientTests
{
    private static (GitBucketClient Client, StubHttpHandler Handler) Create(Func<HttpRequestMessage, string> respond)
    {
        var handler = new StubHttpHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gb/api/v3/") };

        var web = new GitBucketWeb(new HttpClient(handler), "");

        return (new GitBucketClient(httpClient, new HttpClient(handler), web), handler);
    }

    private static int Page(HttpRequestMessage request)
    {
        var match = System.Text.RegularExpressions.Regex.Match(request.RequestUri!.Query, @"[?&]page=(\d+)");

        return int.Parse(match.Groups[1].Value);
    }

    private static string Pull(int number, string? mergedAt = null) =>
        $$"""{"number":{{number}},"title":"t","head":{"ref":"h","sha":"s"},"base":{"ref":"main","sha":"b"},"updated_at":"2026-10-07T10:00:00Z","merged_at":{{(mergedAt is null ? "null" : $"\"{mergedAt}\"")}}}""";

    [Fact]
    public async Task Lists_read_every_page()
    {
        var (client, handler) = Create(request => Page(request) switch
        {
            1 => $"[{Pull(1)},{Pull(2)}]",
            2 => $"[{Pull(3, "2026-10-07T11:00:00Z")}]",
            _ => "[]",
        });

        var pulls = await client.ListPullsAsync("o/r", "closed", default);

        Assert.Equal([1, 2, 3], pulls.Select(pull => pull.Number));
        Assert.True(pulls[2].IsMerged());
        Assert.False(pulls[0].IsMerged());
        Assert.Equal(3, handler.Urls.Count);
        Assert.Contains("state=closed", handler.Urls[0]);
    }

    [Fact]
    public async Task Unreadable_reply_reports_the_response_text()
    {
        var (client, _) = Create(_ => "\"not an object\"");

        var exception = await Assert.ThrowsAsync<GitBucketException>(() => client.GetPullAsync("o/r", 1, default));

        Assert.Contains("GbPull", exception.Message);
        Assert.Contains("not an object", exception.Message);
    }

    [Fact]
    public async Task Created_pull_is_read_back_by_number_because_the_create_reply_is_not_trusted()
    {
        var (client, handler) = Create(
            request => request.Method == HttpMethod.Post ? """{"number":8,"merged":"odd"}""" : Pull(8));

        var newPull = new NewPull("t", "ai/issue-6", "main", "body");
        var pull = await client.CreatePullAsync("o/r", newPull, default);

        Assert.Equal(8, pull.Number);
        Assert.Equal(["/api/v3/repos/o/r/pulls", "/api/v3/repos/o/r/pulls/8"], handler.Urls);
    }

    [Fact]
    public async Task Created_pull_with_string_reply_is_found_by_head_and_base()
    {
        var (client, handler) = Create(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return "\"created\"";
            }

            return Page(request) == 1
                ? """[{"number":7,"head":{"ref":"h"},"base":{"ref":"other"}},{"number":8,"head":{"ref":"h"},"base":{"ref":"main"}}]"""
                : "[]";
        });
        var newPull = new NewPull("t", "h", "main", "body");

        var pull = await client.CreatePullAsync("o/r", newPull, default);

        Assert.Equal(8, pull.Number);
        Assert.Equal(3, handler.Urls.Count);
        Assert.Single(handler.Bodies, body => body.Length > 0);
        Assert.Contains("state=open", handler.Urls[1]);
    }

    [Theory]
    [InlineData("\"not an object\"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"number\":\"8\"}")]
    [InlineData("{\"number\":0}")]
    [InlineData("")]
    public async Task Create_pull_without_number_or_matching_pull_reports_the_response(string reply)
    {
        var (client, handler) = Create(request => request.Method == HttpMethod.Post ? reply : "[]");
        var newPull = new NewPull("t", "h", "main", "body");

        var exception = await Assert.ThrowsAsync<GitBucketException>(
            () => client.CreatePullAsync("o/r", newPull, default));

        Assert.Contains("POST repos/o/r/pulls", exception.Message);
        Assert.Contains("Reply:", exception.Message);
        Assert.Contains(reply.Length == 0 ? "(empty response)" : reply, exception.Message);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Single(handler.Bodies, body => body.Length > 0);
    }

    [Fact]
    public async Task Server_that_ignores_page_does_not_loop()
    {
        var (client, handler) = Create(_ => $"[{Pull(1)},{Pull(2)}]");

        var pulls = await client.ListPullsAsync("o/r", "open", default);

        Assert.Equal(2, pulls.Count);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task Issue_list_drops_pull_requests_and_reads_the_single_assignee()
    {
        var (client, handler) = Create(request => Page(request) > 1 ? "[]" : """
            [
              {"number":1,"title":"issue","body":null,"user":{"login":"a"},"labels":[{"name":"ai"}],"updated_at":"2026-10-07T10:00:00Z","assignee":{"login":"bot"}},
              {"number":2,"title":"a PR","user":{"login":"a"},"labels":[],"updated_at":"2026-10-07T10:00:00Z","pull_request":{"url":"x"}}
            ]
            """);

        var issues = await client.ListOpenIssuesAsync("o/r", "ai", "bot", default);

        var issue = Assert.Single(issues);
        Assert.True(issue.IsAssignedTo("bot"));
        Assert.Contains("labels=ai", handler.Urls[0]);
        Assert.Contains("assignee=bot", handler.Urls[0]);
    }

    [Fact]
    public async Task Closed_issue_list_asks_for_closed_issues_with_the_label_and_drops_pull_requests()
    {
        var (client, handler) = Create(request => Page(request) > 1 ? "[]" : """
            [
              {"number":1,"title":"sub","body":null,"user":{"login":"a"},"labels":[{"name":"ai:sub-issue"}],"updated_at":"2026-10-07T10:00:00Z","state":"closed"},
              {"number":2,"title":"a PR","user":{"login":"a"},"labels":[],"updated_at":"2026-10-07T10:00:00Z","pull_request":{"url":"x"}}
            ]
            """);

        var issues = await client.ListClosedIssuesAsync("o/r", "ai:sub-issue", default);

        Assert.Equal([1], issues.Select(issue => issue.Number));
        Assert.Contains("state=closed", handler.Urls[0]);
        Assert.Contains("labels=ai%3Asub-issue", handler.Urls[0]);
    }

    [Fact]
    public async Task Repo_default_branch_is_mapped()
    {
        var (client, _) = Create(_ => """{"name":"r","full_name":"o/r","default_branch":"develop"}""");

        Assert.Equal("develop", (await client.GetRepoAsync("o/r", default)).DefaultBranch);
    }

    // ---- closing an issue: /api/v3 has no route for it, so the client posts the issue page's form ----

    /// <summary>When the fake ends the session: with a sign-in redirect, or quietly.</summary>
    private enum SessionEnd
    {
        Never,
        OnceWithRedirect,
        OnceQuietly,
        Always,
    }

    /// <summary>
    /// A fake GitBucket for the close flow: the API answers the user and the issue (open until the close form was
    /// posted while signed in with "secret"), the web side answers forms with redirects like GitBucket does.
    /// <see cref="Root"/> is a context path ("/gitbucket"); <see cref="IsRedirectAbsolute"/> sends full URLs in
    /// Location, as Scalatra does. Form bodies come from the handler, which has already read them.
    /// </summary>
    private sealed class CloseServer
    {
        private const string IssueJson =
            """{"number":7,"title":"t","user":{"login":"a"},"updated_at":"2026-10-07T10:00:00Z","state":"STATE"}""";

        public string Root { get; init; } = "";
        public bool IsRedirectAbsolute { get; init; }
        public bool IsCloseIgnored { get; init; }
        public SessionEnd SessionEnd { get; set; }
        public bool IsClosed { get; set; }
        public List<string> Bodies { get; set; } = [];

        private bool _isSignedIn;

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath[Root.Length..];
            if (path == "/api/v3/user")
            {
                return Json("""{"login":"bot"}""");
            }

            if (path.StartsWith("/api/v3/repos/", StringComparison.Ordinal))
            {
                var state = IsClosed ? "closed" : "open";
                return Json(IssueJson.Replace("STATE", state));
            }

            var form = Bodies[^1];
            if (path == "/signin")
            {
                _isSignedIn = form.Contains("password=secret", StringComparison.Ordinal);
                return Redirect(_isSignedIn ? "/" : "/signin");
            }

            if (SessionEnd is SessionEnd.OnceWithRedirect or SessionEnd.Always)
            {
                SessionEnd = SessionEnd == SessionEnd.Always ? SessionEnd.Always : SessionEnd.Never;
                _isSignedIn = false;
            }

            if (!_isSignedIn)
            {
                return Redirect("/signin?redirect=%2Fo%2Fissues%2Fissues%2F7");
            }

            // A session GitBucket dropped while still answering the form as usual: nothing is closed.
            if (SessionEnd == SessionEnd.OnceQuietly)
            {
                SessionEnd = SessionEnd.Never;
                _isSignedIn = false;
                return Redirect("/o/issues/issues/7");
            }

            IsClosed = !IsCloseIgnored && form.Contains("action=close", StringComparison.Ordinal);
            return Redirect("/o/issues/issues/7");
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            var target = Root + location;
            response.Headers.Location = IsRedirectAbsolute
                ? new Uri("http://gb" + target)
                : new Uri(target, UriKind.Relative);

            return response;
        }
    }

    private static (GitBucketClient Client, StubHttpHandler Handler) CreateForClose(
        CloseServer server,
        string password = "secret")
    {
        var handler = new StubHttpHandler(server.Respond);
        server.Bodies = handler.Bodies;
        var apiClient = new HttpClient(handler) { BaseAddress = new Uri($"http://gb{server.Root}/api/v3/") };
        var webClient = new HttpClient(handler) { BaseAddress = new Uri($"http://gb{server.Root}/") };
        var web = new GitBucketWeb(webClient, password);

        return (new GitBucketClient(apiClient, new HttpClient(handler), web), handler);
    }

    /// <summary>The first close signs in; then the issue reopens and the session ends before the next close.</summary>
    private static async Task CloseOnceThenEndTheSessionAsync(
        GitBucketClient client,
        CloseServer server,
        SessionEnd sessionEnd)
    {
        await client.CloseIssueAsync("o/issues", 7, default);
        server.IsClosed = false;
        server.SessionEnd = sessionEnd;
    }

    [Fact]
    public async Task Closing_an_issue_signs_in_as_the_token_user_and_posts_the_close_form_of_its_own_repo()
    {
        var server = new CloseServer();
        var (client, handler) = CreateForClose(server);

        await client.CloseIssueAsync("o/issues", 7, default);

        Assert.True(server.IsClosed);
        Assert.Equal(
            [
                "/api/v3/repos/o/issues/issues/7", "/api/v3/user", "/signin", "/o/issues/issue_comments/state",
                "/api/v3/repos/o/issues/issues/7",
            ],
            handler.Urls);
        Assert.Equal("userName=bot&password=secret", handler.Bodies[2]);
        Assert.Equal("issueId=7&action=close", handler.Bodies[3]);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("/gitbucket", false)]
    [InlineData("/gitbucket", true)]
    public async Task A_context_path_and_absolute_redirects_work_like_relative_ones(
        string root,
        bool isRedirectAbsolute)
    {
        var server = new CloseServer { Root = root, IsRedirectAbsolute = isRedirectAbsolute };
        var (client, handler) = CreateForClose(server);
        await CloseOnceThenEndTheSessionAsync(client, server, SessionEnd.OnceWithRedirect);

        await client.CloseIssueAsync("o/issues", 7, default);

        Assert.True(server.IsClosed);
        Assert.Contains(root + "/o/issues/issue_comments/state", handler.Urls);
        Assert.Equal(2, handler.Urls.Count(url => url == root + "/signin"));
    }

    [Fact]
    public async Task A_closed_issue_is_left_alone_without_signing_in()
    {
        var (client, handler) = CreateForClose(new CloseServer { IsClosed = true });

        await client.CloseIssueAsync("o/issues", 7, default);

        Assert.Equal(["/api/v3/repos/o/issues/issues/7"], handler.Urls);
    }

    [Fact]
    public async Task The_session_is_kept_and_signed_in_again_once_it_has_ended()
    {
        var server = new CloseServer();
        var (client, handler) = CreateForClose(server);
        await CloseOnceThenEndTheSessionAsync(client, server, SessionEnd.OnceWithRedirect);
        handler.Urls.Clear();

        await client.CloseIssueAsync("o/issues", 7, default);

        Assert.True(server.IsClosed);
        Assert.Equal(
            [
                "/api/v3/repos/o/issues/issues/7", "/o/issues/issue_comments/state", "/api/v3/user", "/signin",
                "/o/issues/issue_comments/state", "/api/v3/repos/o/issues/issues/7",
            ],
            handler.Urls);
    }

    [Fact]
    public async Task A_session_lost_without_a_sign_in_redirect_fails_once_and_the_next_close_signs_in_again()
    {
        var server = new CloseServer();
        var (client, handler) = CreateForClose(server);
        await CloseOnceThenEndTheSessionAsync(client, server, SessionEnd.OnceQuietly);
        await Assert.ThrowsAsync<GitBucketException>(() => client.CloseIssueAsync("o/issues", 7, default));
        handler.Urls.Clear();

        await client.CloseIssueAsync("o/issues", 7, default);

        Assert.True(server.IsClosed);
        Assert.Contains("/signin", handler.Urls);
    }

    [Fact]
    public async Task A_refused_sign_in_fails_before_the_close_form()
    {
        var (client, handler) = CreateForClose(new CloseServer(), password: "wrong");

        var exception = await Assert.ThrowsAsync<GitBucketException>(
            () => client.CloseIssueAsync("o/issues", 7, default));

        Assert.Contains("git_bucket.web_password", exception.Message);
        Assert.DoesNotContain("/o/issues/issue_comments/state", handler.Urls);
    }

    [Fact]
    public async Task An_issue_still_open_after_the_form_fails_the_close()
    {
        var (client, _) = CreateForClose(new CloseServer { IsCloseIgnored = true });

        var exception = await Assert.ThrowsAsync<GitBucketException>(
            () => client.CloseIssueAsync("o/issues", 7, default));

        Assert.Contains("still open", exception.Message);
        Assert.Contains("write access to o/issues", exception.Message);
    }

    [Fact]
    public async Task A_sign_in_that_does_not_hold_is_named_in_the_error_instead_of_write_access()
    {
        var (client, _) = CreateForClose(new CloseServer { SessionEnd = SessionEnd.Always });

        var exception = await Assert.ThrowsAsync<GitBucketException>(
            () => client.CloseIssueAsync("o/issues", 7, default));

        Assert.Contains("still asks to sign in", exception.Message);
        Assert.DoesNotContain("write access", exception.Message);
    }

    [Fact]
    public async Task Without_a_web_password_the_close_says_how_to_set_it()
    {
        var (client, handler) = CreateForClose(new CloseServer(), password: "");

        var exception = await Assert.ThrowsAsync<GitBucketException>(
            () => client.CloseIssueAsync("o/issues", 7, default));

        Assert.Contains("set git_bucket.web_password in repos.yml", exception.Message);
        Assert.Equal(["/api/v3/repos/o/issues/issues/7"], handler.Urls);
    }

    [Fact]
    public async Task Comments_are_ascii_and_carry_the_hidden_and_the_visible_marker()
    {
        var (client, handler) = Create(_ => """{"id":5,"body":"x","user":{"login":"bot"},"created_at":"2026-10-07T10:00:00Z"}""");

        await client.AddCommentAsync("o/r", 7, "Xong roi – “ok” 🤖", default);

        var sent = handler.Bodies.Single();
        Assert.True(sent.All(character => character < 128));

        var body = System.Text.Json.JsonDocument.Parse(sent).RootElement.GetProperty("body").GetString()!;
        Assert.StartsWith(BotComments.Marker + "\n" + BotComments.VisibleTag, body);
        Assert.EndsWith("Xong roi - \"ok\" ", body);
    }
}
