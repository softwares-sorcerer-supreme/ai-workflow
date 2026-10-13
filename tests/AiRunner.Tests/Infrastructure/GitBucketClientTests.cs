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

        return (new GitBucketClient(httpClient, new HttpClient(handler)), handler);
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

    [Fact]
    public async Task Closing_an_issue_patches_its_state_in_its_own_repo()
    {
        var (client, handler) = Create(_ => """{"number":7,"title":"t","state":"closed"}""");

        await client.CloseIssueAsync("o/issues", 7, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/v3/repos/o/issues/issues/7", request.RequestUri!.AbsolutePath);
        Assert.Equal("closed", System.Text.Json.JsonDocument.Parse(handler.Bodies.Single()).RootElement.GetProperty("state").GetString());
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
