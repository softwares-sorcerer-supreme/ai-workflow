using System.Net;
using System.Text;
using System.Text.Json;
using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>
/// The /api/v3 calls the runner uses (design doc section 10). "repo" is always "owner/name".
/// One interface, so a GitBucket API difference is fixed in one place.
/// </summary>
public interface IGitBucketClient
{
    Task<GbUser> GetCurrentUserAsync(CancellationToken ct);
    Task<GbRepo> GetRepoAsync(string repo, CancellationToken ct);

    /// <summary>Open issues (no PRs), all pages. Null filters are not sent.</summary>
    Task<IReadOnlyList<GbIssue>> ListOpenIssuesAsync(string repo, string? label, string? assignee, CancellationToken ct);
    Task<GbIssue> GetIssueAsync(string repo, int number, CancellationToken ct);
    Task<IReadOnlyList<GbComment>> ListCommentsAsync(string repo, int number, CancellationToken ct);
    Task<GbIssue> CreateIssueAsync(string repo, string title, string body, IEnumerable<string> labels, CancellationToken ct);

    Task<GbComment> AddCommentAsync(string repo, int number, string body, CancellationToken ct);
    Task<GbComment> UpdateCommentAsync(string repo, long commentId, string body, CancellationToken ct);

    Task AddLabelsAsync(string repo, int number, IEnumerable<string> labels, CancellationToken ct);
    /// <summary>No error when the label is not on the issue.</summary>
    Task RemoveLabelAsync(string repo, int number, string label, CancellationToken ct);
    /// <summary>Creates a repo label; returns false when it already exists.</summary>
    Task<bool> CreateLabelAsync(string repo, string name, string color, string description, CancellationToken ct);

    /// <summary>state = open | closed | all.</summary>
    Task<IReadOnlyList<GbPull>> ListPullsAsync(string repo, string state, CancellationToken ct);
    Task<GbPull> GetPullAsync(string repo, int number, CancellationToken ct);
    Task<GbPull> CreatePullAsync(string repo, string title, string head, string baseBranch, string body, CancellationToken ct);
}

/// <summary>
/// Every text sent to GitBucket goes through AsciiSanitizer, because this GitBucket returns
/// HTTP 500 on non-ASCII text. Every comment gets the bot marker. Lists read every page.
/// </summary>
public sealed class GitBucketClient(HttpClient http) : IGitBucketClient
{
    /// <summary>Safety stop for lists; 50 pages is far more than a pilot repo has.</summary>
    private const int MaxPages = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ---- read ----

    public Task<GbUser> GetCurrentUserAsync(CancellationToken ct) => GetAsync<GbUser>("user", ct);

    public Task<GbRepo> GetRepoAsync(string repo, CancellationToken ct) => GetAsync<GbRepo>($"repos/{repo}", ct);

    public async Task<IReadOnlyList<GbIssue>> ListOpenIssuesAsync(string repo, string? label, string? assignee, CancellationToken ct)
    {
        // GitBucket ignores "since", so the poller compares updated_at and labels itself.
        var filter = (label is null ? "" : $"&labels={Uri.EscapeDataString(label)}") +
                     (assignee is null ? "" : $"&assignee={Uri.EscapeDataString(assignee)}");
        var items = await GetPagesAsync<GbIssue>($"repos/{repo}/issues?state=open{filter}", i => i.Number, ct);
        return items.Where(i => !i.IsPullRequest()).ToList();
    }

    public Task<GbIssue> GetIssueAsync(string repo, int number, CancellationToken ct) =>
        GetAsync<GbIssue>($"repos/{repo}/issues/{number}", ct);

    public async Task<IReadOnlyList<GbComment>> ListCommentsAsync(string repo, int number, CancellationToken ct) =>
        await GetPagesAsync<GbComment>($"repos/{repo}/issues/{number}/comments", c => c.Id, ct);

    public async Task<IReadOnlyList<GbPull>> ListPullsAsync(string repo, string state, CancellationToken ct) =>
        await GetPagesAsync<GbPull>($"repos/{repo}/pulls?state={state}", p => p.Number, ct);

    public Task<GbPull> GetPullAsync(string repo, int number, CancellationToken ct) =>
        GetAsync<GbPull>($"repos/{repo}/pulls/{number}", ct);

    // ---- write ----

    public Task<GbIssue> CreateIssueAsync(string repo, string title, string body, IEnumerable<string> labels, CancellationToken ct) =>
        SendAsync<GbIssue>(HttpMethod.Post, $"repos/{repo}/issues",
            new { title = AsciiSanitizer.Clean(title), body = AsciiSanitizer.Clean(body), labels = labels.ToArray() }, ct);

    public Task<GbComment> AddCommentAsync(string repo, int number, string body, CancellationToken ct) =>
        SendAsync<GbComment>(HttpMethod.Post, $"repos/{repo}/issues/{number}/comments",
            new { body = WithMarker(body) }, ct);

    public Task<GbComment> UpdateCommentAsync(string repo, long commentId, string body, CancellationToken ct) =>
        SendAsync<GbComment>(HttpMethod.Patch, $"repos/{repo}/issues/comments/{commentId}",
            new { body = WithMarker(body) }, ct);

    public async Task AddLabelsAsync(string repo, int number, IEnumerable<string> labels, CancellationToken ct)
    {
        var list = labels.ToArray();
        if (list.Length == 0) return;
        await SendAsync<JsonElement>(HttpMethod.Post, $"repos/{repo}/issues/{number}/labels", list, ct);
    }

    public async Task RemoveLabelAsync(string repo, int number, string label, CancellationToken ct)
    {
        try
        {
            await SendAsync<JsonElement>(HttpMethod.Delete,
                $"repos/{repo}/issues/{number}/labels/{Uri.EscapeDataString(label)}", null, ct);
        }
        catch (GitBucketException ex) when (ex.Status == HttpStatusCode.NotFound) { /* label was not there */ }
    }

    public async Task<bool> CreateLabelAsync(string repo, string name, string color, string description, CancellationToken ct)
    {
        try
        {
            await SendAsync<JsonElement>(HttpMethod.Post, $"repos/{repo}/labels",
                new { name, color, description = AsciiSanitizer.Clean(description) }, ct);
            return true;
        }
        catch (GitBucketException ex) when (ex.Status is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<GbPull> CreatePullAsync(string repo, string title, string head, string baseBranch, string body, CancellationToken ct)
    {
        // GitBucket's create reply is not readable as GbPull; only the number is trusted, the PR is read back with GET.
        var created = await SendAsync<JsonElement>(HttpMethod.Post, $"repos/{repo}/pulls",
            new { title = AsciiSanitizer.Clean(title), head, @base = baseBranch, body = WithMarker(body) }, ct);
        if (!created.TryGetProperty("number", out var number) || !number.TryGetInt32(out var n))
            throw new GitBucketException(HttpStatusCode.OK, $"POST repos/{repo}/pulls: the reply has no PR number. Reply: {Text.Short(created.GetRawText())}");
        return await GetPullAsync(repo, n, ct);
    }

    // ---- helpers ----

    private static string WithMarker(string body)
    {
        var clean = AsciiSanitizer.Clean(body);
        return clean.Contains(BotComments.Marker, StringComparison.Ordinal)
            ? clean
            : $"{BotComments.Marker}\n{BotComments.VisibleTag}\n\n{clean}";
    }

    private Task<T> GetAsync<T>(string url, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, url, null, ct);

    /// <summary>
    /// GitHub-style paging: page=1, 2, ... until a page brings nothing new. A server that ignores
    /// "page" returns the same items again, which also stops the loop.
    /// </summary>
    private async Task<List<T>> GetPagesAsync<T>(string url, Func<T, long> key, CancellationToken ct)
    {
        var all = new List<T>();
        var seen = new HashSet<long>();
        var sep = url.Contains('?') ? '&' : '?';
        for (var page = 1; page <= MaxPages; page++)
        {
            var items = await GetAsync<List<T>>($"{url}{sep}per_page=100&page={page}", ct) ?? [];
            var fresh = items.Where(i => seen.Add(key(i))).ToList();
            if (fresh.Count == 0) break;
            all.AddRange(fresh);
        }
        return all;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new GitBucketException(res.StatusCode, $"{method} {url} -> {(int)res.StatusCode}: {Text.Short(text)}");

        if (string.IsNullOrWhiteSpace(text)) return default!;
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json)!;
        }
        catch (JsonException ex)
        {
            // The raw reply is the only way to see which GitBucket quirk broke the model.
            throw new GitBucketException(res.StatusCode,
                $"{method} {url}: cannot read the reply as {typeof(T).Name} ({ex.Message}). Reply: {Text.Short(text)}");
        }
    }
}

public sealed class GitBucketException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
