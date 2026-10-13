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
    Task<GbUser> GetCurrentUserAsync(CancellationToken cancellationToken);

    Task<GbRepo> GetRepoAsync(string repo, CancellationToken cancellationToken);

    /// <summary>Open issues (no PRs), all pages. Null filters are not sent.</summary>
    Task<IReadOnlyList<GbIssue>> ListOpenIssuesAsync(
        string repo,
        string? label,
        string? assignee,
        CancellationToken cancellationToken);

    /// <summary>Closed issues (no PRs) with the label, all pages.</summary>
    Task<IReadOnlyList<GbIssue>> ListClosedIssuesAsync(
        string repo,
        string label,
        CancellationToken cancellationToken);

    Task<GbIssue> GetIssueAsync(string repo, int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<GbComment>> ListCommentsAsync(string repo, int number, CancellationToken cancellationToken);

    Task<GbIssue> CreateIssueAsync(string repo, NewIssue issue, CancellationToken cancellationToken);

    Task<GbComment> AddCommentAsync(string repo, int number, string body, CancellationToken cancellationToken);

    Task<GbComment> UpdateCommentAsync(string repo, long commentId, string body, CancellationToken cancellationToken);

    Task AddLabelsAsync(string repo, int number, IEnumerable<string> labels, CancellationToken cancellationToken);

    /// <summary>Closes an issue (state = closed); nothing happens when it is closed already.</summary>
    Task CloseIssueAsync(string repo, int number, CancellationToken cancellationToken);

    /// <summary>No error when the label is not on the issue.</summary>
    Task RemoveLabelAsync(string repo, int number, string label, CancellationToken cancellationToken);

    /// <summary>Creates a repo label; returns false when it already exists.</summary>
    Task<bool> CreateLabelAsync(string repo, NewLabel label, CancellationToken cancellationToken);

    /// <summary>state = open | closed | all.</summary>
    Task<IReadOnlyList<GbPull>> ListPullsAsync(string repo, string state, CancellationToken cancellationToken);

    Task<GbPull> GetPullAsync(string repo, int number, CancellationToken cancellationToken);

    Task<GbPull> CreatePullAsync(string repo, NewPull pull, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads one _attached file into <see cref="AttachmentDownload.Directory"/>. Throws
    /// <see cref="AttachmentDownloadException"/> with a short reason when it cannot be had.
    /// </summary>
    Task<DownloadedAttachment> DownloadAttachmentAsync(AttachmentDownload request, CancellationToken cancellationToken);
}

/// <param name="Url">Absolute _attached URL; must be on the GitBucket host.</param>
/// <param name="Directory">Where the file goes.</param>
/// <param name="FileStem">The file name without extension; the extension is chosen from what the server says.</param>
/// <param name="MaxBytes">Larger files are refused (and never kept).</param>
public sealed record AttachmentDownload(string Url, string Directory, string FileStem, long MaxBytes);

/// <param name="Path">The saved file.</param>
/// <param name="OriginalName">The name the server gave (Content-Disposition), or null.</param>
public sealed record DownloadedAttachment(string Path, string? OriginalName, string? ContentType, long Bytes);

/// <summary>
/// Every text sent to GitBucket goes through AsciiSanitizer, because this GitBucket returns
/// HTTP 500 on non-ASCII text. Every comment gets the bot marker. Lists read every page.
/// </summary>
public sealed class GitBucketClient(HttpClient httpClient, HttpClient downloadClient) : IGitBucketClient
{
    /// <summary>Safety stop for lists; 50 pages is far more than a pilot repo has.</summary>
    private const int MaxPages = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ---- read ----

    public Task<GbUser> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        GetAsync<GbUser>("user", cancellationToken);

    public Task<GbRepo> GetRepoAsync(string repo, CancellationToken cancellationToken) =>
        GetAsync<GbRepo>($"repos/{repo}", cancellationToken);

    public async Task<IReadOnlyList<GbIssue>> ListOpenIssuesAsync(
        string repo,
        string? label,
        string? assignee,
        CancellationToken cancellationToken)
    {
        // GitBucket ignores "since", so the poller compares updated_at and labels itself.
        var filter = (label is null ? "" : $"&labels={Uri.EscapeDataString(label)}") +
                     (assignee is null ? "" : $"&assignee={Uri.EscapeDataString(assignee)}");
        var items = await GetPagesAsync<GbIssue>(
            $"repos/{repo}/issues?state=open{filter}",
            issue => issue.Number,
            cancellationToken);

        return items.Where(issue => !issue.IsPullRequest()).ToList();
    }

    public async Task<IReadOnlyList<GbIssue>> ListClosedIssuesAsync(
        string repo,
        string label,
        CancellationToken cancellationToken)
    {
        var items = await GetPagesAsync<GbIssue>(
            $"repos/{repo}/issues?state=closed&labels={Uri.EscapeDataString(label)}",
            issue => issue.Number,
            cancellationToken);

        return items.Where(issue => !issue.IsPullRequest()).ToList();
    }

    public Task<GbIssue> GetIssueAsync(string repo, int number, CancellationToken cancellationToken) =>
        GetAsync<GbIssue>($"repos/{repo}/issues/{number}", cancellationToken);

    public async Task<IReadOnlyList<GbComment>> ListCommentsAsync(
        string repo,
        int number,
        CancellationToken cancellationToken) =>
        await GetPagesAsync<GbComment>(
            $"repos/{repo}/issues/{number}/comments",
            comment => comment.Id,
            cancellationToken);

    public async Task<IReadOnlyList<GbPull>> ListPullsAsync(
        string repo,
        string state,
        CancellationToken cancellationToken) =>
        await GetPagesAsync<GbPull>($"repos/{repo}/pulls?state={state}", pull => pull.Number, cancellationToken);

    public Task<GbPull> GetPullAsync(string repo, int number, CancellationToken cancellationToken) =>
        GetAsync<GbPull>($"repos/{repo}/pulls/{number}", cancellationToken);

    // ---- write ----

    public Task<GbIssue> CreateIssueAsync(string repo, NewIssue issue, CancellationToken cancellationToken)
    {
        var cleanTitle = AsciiSanitizer.Clean(issue.Title);
        var cleanBody = AsciiSanitizer.Clean(issue.Body);
        var labelNames = issue.Labels.ToArray();

        return SendAsync<GbIssue>(
            HttpMethod.Post,
            $"repos/{repo}/issues",
            new
            {
                title = cleanTitle,
                body = cleanBody,
                labels = labelNames
            },
            cancellationToken);
    }

    public Task<GbComment> AddCommentAsync(
        string repo,
        int number,
        string body,
        CancellationToken cancellationToken)
    {
        var markedBody = WithMarker(body);

        return SendAsync<GbComment>(
            HttpMethod.Post,
            $"repos/{repo}/issues/{number}/comments",
            new
            {
                body = markedBody
            },
            cancellationToken);
    }

    public Task<GbComment> UpdateCommentAsync(
        string repo,
        long commentId,
        string body,
        CancellationToken cancellationToken)
    {
        var markedBody = WithMarker(body);

        return SendAsync<GbComment>(
            HttpMethod.Patch,
            $"repos/{repo}/issues/comments/{commentId}",
            new
            {
                body = markedBody
            },
            cancellationToken);
    }

    public async Task CloseIssueAsync(string repo, int number, CancellationToken cancellationToken)
    {
        await SendAsync<JsonElement>(
            HttpMethod.Patch,
            $"repos/{repo}/issues/{number}",
            new
            {
                state = "closed"
            },
            cancellationToken);
    }

    public async Task AddLabelsAsync(
        string repo,
        int number,
        IEnumerable<string> labels,
        CancellationToken cancellationToken)
    {
        var labelNames = labels.ToArray();
        if (labelNames.Length == 0)
        {
            return;
        }

        await SendAsync<JsonElement>(
            HttpMethod.Post,
            $"repos/{repo}/issues/{number}/labels",
            labelNames,
            cancellationToken);
    }

    public async Task RemoveLabelAsync(string repo, int number, string label, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync<JsonElement>(
                HttpMethod.Delete,
                $"repos/{repo}/issues/{number}/labels/{Uri.EscapeDataString(label)}",
                null,
                cancellationToken);
        }
        catch (GitBucketException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            // The label was not there.
        }
    }

    public async Task<bool> CreateLabelAsync(string repo, NewLabel label, CancellationToken cancellationToken)
    {
        try
        {
            var cleanDescription = AsciiSanitizer.Clean(label.Description);
            await SendAsync<JsonElement>(
                HttpMethod.Post,
                $"repos/{repo}/labels",
                new
                {
                    name = label.Name,
                    color = label.Color,
                    description = cleanDescription
                },
                cancellationToken);

            return true;
        }
        catch (GitBucketException exception)
            when (exception.Status is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<GbPull> CreatePullAsync(string repo, NewPull pull, CancellationToken cancellationToken)
    {
        // GitBucket's create reply is not readable as GbPull; only the number is trusted, the PR is read back with GET.
        var cleanTitle = AsciiSanitizer.Clean(pull.Title);
        var markedBody = WithMarker(pull.Body);
        var created = await SendAsync<JsonElement>(
            HttpMethod.Post,
            $"repos/{repo}/pulls",
            new
            {
                title = cleanTitle,
                head = pull.Head,
                @base = pull.BaseBranch,
                body = markedBody
            },
            cancellationToken);

        if (created.ValueKind == JsonValueKind.Object &&
            created.TryGetProperty("number", out var numberElement) &&
            numberElement.ValueKind == JsonValueKind.Number &&
            numberElement.TryGetInt32(out var pullNumber) && pullNumber > 0)
        {
            return await GetPullAsync(repo, pullNumber, cancellationToken);
        }

        var openPulls = await ListPullsAsync(repo, "open", cancellationToken);
        var matchingPull = openPulls.Find(pull.Head, pull.BaseBranch);
        if (matchingPull is not null)
        {
            return matchingPull;
        }

        var reply = created.ValueKind == JsonValueKind.Undefined ? "(empty response)" : created.GetRawText();
        var shortReply = Text.Short(reply);
        throw new GitBucketException(
            HttpStatusCode.OK,
            $"POST repos/{repo}/pulls: the reply has no usable PR number and no open PR matches " +
            $"'{pull.Head}' into '{pull.BaseBranch}'. Reply: {shortReply}");
    }

    /// <summary>
    /// _attached is a web route, not part of /api/v3. The token goes only to the GitBucket host over the same scheme,
    /// redirects are not followed (a redirect to the login page means the token is not accepted here), and the size
    /// cap is enforced while reading, so a huge file never lands on disk. The download client must not follow redirects.
    /// </summary>
    public async Task<DownloadedAttachment> DownloadAttachmentAsync(
        AttachmentDownload download,
        CancellationToken cancellationToken)
    {
        var (url, directory, fileStem, maxBytes) = download;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsOnGitBucket(uri))
        {
            throw new AttachmentDownloadException("the link is not on the GitBucket host");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await downloadClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            var location = response.Headers.Location?.ToString() ?? "?";
            throw new AttachmentDownloadException(
                $"redirected to {Text.Short(location, 80)} ({status}); the token is probably not accepted for attachments");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AttachmentDownloadException($"the server answered {status}");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > maxBytes)
        {
            throw new AttachmentDownloadException($"too large ({AttachmentReport.Size(contentLength.Value)})");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var disposition = response.Content.Headers.ContentDisposition;
        var originalName = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        var extension = AttachmentPolicy.ChooseExtension(originalName, uri.AbsolutePath, contentType);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + extension);
        long total = 0;
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxBytes)
                {
                    throw new AttachmentDownloadException($"too large (over {AttachmentReport.Size(maxBytes)})");
                }

                var chunk = buffer.AsMemory(0, read);
                await target.WriteAsync(chunk, cancellationToken);
            }
        }
        catch
        {
            FileSystem.DeleteFile(path);
            throw;
        }

        return new DownloadedAttachment(path, originalName, contentType, total);
    }

    // ---- helpers ----

    /// <summary>Same scheme, host and port as the API: the token is never sent anywhere else.</summary>
    private bool IsOnGitBucket(Uri uri)
    {
        var gitBucket = httpClient.BaseAddress;

        return gitBucket is not null &&
               string.Equals(uri.Scheme, gitBucket.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Authority, gitBucket.Authority, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithMarker(string body)
    {
        var clean = AsciiSanitizer.Clean(body);
        return clean.Contains(BotComments.Marker, StringComparison.Ordinal)
            ? clean
            : $"{BotComments.Marker}\n{BotComments.VisibleTag}\n\n{clean}";
    }

    private Task<T> GetAsync<T>(string url, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Get, url, null, cancellationToken);

    /// <summary>
    /// GitHub-style paging: page=1, 2, ... until a page brings nothing new. A server that ignores
    /// "page" returns the same items again, which also stops the loop.
    /// </summary>
    private async Task<List<T>> GetPagesAsync<T>(
        string url,
        Func<T, long> keySelector,
        CancellationToken cancellationToken)
    {
        var allItems = new List<T>();
        var seenKeys = new HashSet<long>();
        var separator = url.Contains('?') ? '&' : '?';

        for (var page = 1; page <= MaxPages; page++)
        {
            var items = await GetAsync<List<T>>($"{url}{separator}per_page=100&page={page}", cancellationToken) ?? [];
            var newItems = items.Where(item => seenKeys.Add(keySelector(item))).ToList();
            if (newItems.Count == 0)
            {
                break;
            }

            allItems.AddRange(newItems);
        }

        return allItems;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            var jsonBody = JsonSerializer.Serialize(body, Json);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitBucketException(
                response.StatusCode,
                $"{method} {url} -> {(int)response.StatusCode}: {Text.Short(text)}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return default!;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Json)!;
        }
        catch (JsonException exception)
        {
            // The raw reply is the only way to see which GitBucket quirk broke the model.
            throw new GitBucketException(
                response.StatusCode,
                $"{method} {url}: cannot read the reply as {typeof(T).Name} ({exception.Message}). Reply: {Text.Short(text)}");
        }
    }
}

/// <summary>An attachment that cannot be downloaded; the message is a short reason for the context file.</summary>
public sealed class AttachmentDownloadException(string reason) : Exception(reason);

public sealed class GitBucketException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
