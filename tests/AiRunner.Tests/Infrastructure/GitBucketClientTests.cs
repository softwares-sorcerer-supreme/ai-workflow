using System.Net;
using System.Text;
using AiRunner.Infrastructure;

namespace AiRunner.Tests.Infrastructure;

/// <summary>GitBucketClient against canned JSON: paging, field mapping, what is sent.</summary>
public class GitBucketClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") };
        }
    }

    private static (GitBucketClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, string> respond)
    {
        var handler = new StubHandler(respond);
        return (new GitBucketClient(new HttpClient(handler) { BaseAddress = new Uri("http://gb/api/v3/") }), handler);
    }

    private static int Page(HttpRequestMessage r) =>
        int.Parse(System.Text.RegularExpressions.Regex.Match(r.RequestUri!.Query, @"[?&]page=(\d+)").Groups[1].Value);

    private static string Pull(int n, string? mergedAt = null) =>
        $$"""{"number":{{n}},"title":"t","head":{"ref":"h","sha":"s"},"base":{"ref":"main","sha":"b"},"updated_at":"2026-10-07T10:00:00Z","merged_at":{{(mergedAt is null ? "null" : $"\"{mergedAt}\"")}}}""";

    [Fact]
    public async Task Lists_read_every_page()
    {
        var (client, handler) = Create(r => Page(r) switch
        {
            1 => $"[{Pull(1)},{Pull(2)}]",
            2 => $"[{Pull(3, "2026-10-07T11:00:00Z")}]",
            _ => "[]",
        });

        var pulls = await client.ListPullsAsync("o/r", "closed", default);

        Assert.Equal([1, 2, 3], pulls.Select(p => p.Number));
        Assert.True(pulls[2].IsMerged());
        Assert.False(pulls[0].IsMerged());
        Assert.Equal(3, handler.Urls.Count);
        Assert.Contains("state=closed", handler.Urls[0]);
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
        var (client, handler) = Create(r => Page(r) > 1 ? "[]" : """
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
    public async Task Repo_default_branch_is_mapped()
    {
        var (client, _) = Create(_ => """{"name":"r","full_name":"o/r","default_branch":"develop"}""");
        Assert.Equal("develop", (await client.GetRepoAsync("o/r", default)).DefaultBranch);
    }

    [Fact]
    public async Task Comments_are_ascii_and_carry_the_hidden_and_the_visible_marker()
    {
        var (client, handler) = Create(_ => """{"id":5,"body":"x","user":{"login":"bot"},"created_at":"2026-10-07T10:00:00Z"}""");

        await client.AddCommentAsync("o/r", 7, "Xong roi – “ok” 🤖", default);

        var sent = handler.Bodies.Single();
        Assert.True(sent.All(c => c < 128));
        var body = System.Text.Json.JsonDocument.Parse(sent).RootElement.GetProperty("body").GetString()!;
        Assert.StartsWith(BotComments.Marker + "\n" + BotComments.VisibleTag, body);
        Assert.EndsWith("Xong roi - \"ok\" ", body);
    }
}
