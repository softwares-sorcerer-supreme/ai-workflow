using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiRunner.Core;

namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// GitBucket's /api/v3 (GitHub-compatible, design doc section 10) plus the web routes it lacks. Every text sent to
/// GitBucket goes through AsciiSanitizer, because this GitBucket returns HTTP 500 on non-ASCII text. Every comment
/// gets the bot marker. Lists read every page.
/// The token goes on each request, never on the client (ADR 0004): a token saved on the Setup page is used from the
/// next request on, and a request already sent keeps the old one.
/// </summary>
/// <param name="baseUrl">The GitBucket root (code_host.base_url), where _attached links live.</param>
public sealed class GitBucketCodeHost(
    HttpClient httpClient,
    HttpClient downloadClient,
    GitBucketWeb web,
    RunnerCredentials credentials,
    string baseUrl)
    : ICodeHost
{
    /// <summary>Safety stop for lists; 50 pages is far more than a pilot repo has.</summary>
    private const int MaxPages = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Some GitBucket versions ignore the labels of a new issue.</summary>
    public CodeHostCapabilities Capabilities { get; } = new(KeepsLabelsOnCreate: false);

    /// <summary>GitBucket's JSON has no attachment list: the _attached links in the text are all there is.</summary>
    public IReadOnlyList<AttachmentRef> FindAttachments(string? text) => AttachmentLinks.Find(text, baseUrl);

    // ---- read ----

    public Task<HostUser> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        GetAsync<HostUser>("user", cancellationToken);

    public Task<HostRepo> GetRepoAsync(string repo, CancellationToken cancellationToken) =>
        GetAsync<HostRepo>($"repos/{repo}", cancellationToken);

    public async Task<IReadOnlyList<HostIssue>> ListOpenIssuesAsync(
        string repo,
        string? label,
        string? assignee,
        CancellationToken cancellationToken)
    {
        // GitBucket ignores "since", so the poller compares updated_at and labels itself.
        var filter = (label is null ? "" : $"&labels={Uri.EscapeDataString(label)}") +
                     (assignee is null ? "" : $"&assignee={Uri.EscapeDataString(assignee)}");
        var items = await GetPagesAsync<HostIssue>(
            $"repos/{repo}/issues?state=open{filter}",
            issue => issue.Number,
            cancellationToken);

        return items.Where(issue => !issue.IsPullRequest()).ToList();
    }

    public async Task<IReadOnlyList<HostIssue>> ListClosedIssuesAsync(
        string repo,
        string label,
        CancellationToken cancellationToken)
    {
        var items = await GetPagesAsync<HostIssue>(
            $"repos/{repo}/issues?state=closed&labels={Uri.EscapeDataString(label)}",
            issue => issue.Number,
            cancellationToken);

        return items.Where(issue => !issue.IsPullRequest()).ToList();
    }

    public Task<HostIssue> GetIssueAsync(string repo, int number, CancellationToken cancellationToken) =>
        GetAsync<HostIssue>($"repos/{repo}/issues/{number}", cancellationToken);

    public async Task<IReadOnlyList<HostComment>> ListCommentsAsync(
        string repo,
        int number,
        CancellationToken cancellationToken) =>
        await GetPagesAsync<HostComment>(
            $"repos/{repo}/issues/{number}/comments",
            comment => comment.Id,
            cancellationToken);

    public async Task<IReadOnlyList<HostPull>> ListPullsAsync(
        string repo,
        string state,
        CancellationToken cancellationToken) =>
        await GetPagesAsync<HostPull>($"repos/{repo}/pulls?state={state}", pull => pull.Number, cancellationToken);

    public Task<HostPull> GetPullAsync(string repo, int number, CancellationToken cancellationToken) =>
        GetAsync<HostPull>($"repos/{repo}/pulls/{number}", cancellationToken);

    // ---- write ----

    public Task<HostIssue> CreateIssueAsync(string repo, NewIssue issue, CancellationToken cancellationToken)
    {
        var cleanTitle = AsciiSanitizer.Clean(issue.Title);
        var cleanBody = AsciiSanitizer.Clean(issue.Body);
        var labelNames = issue.Labels.ToArray();

        return SendAsync<HostIssue>(
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

    public Task<HostComment> AddCommentAsync(
        string repo,
        int number,
        string body,
        CancellationToken cancellationToken)
    {
        var markedBody = WithMarker(body);

        return SendAsync<HostComment>(
            HttpMethod.Post,
            $"repos/{repo}/issues/{number}/comments",
            new
            {
                body = markedBody
            },
            cancellationToken);
    }

    public Task<HostComment> UpdateCommentAsync(
        string repo,
        long commentId,
        string body,
        CancellationToken cancellationToken)
    {
        var markedBody = WithMarker(body);

        return SendAsync<HostComment>(
            HttpMethod.Patch,
            $"repos/{repo}/issues/comments/{commentId}",
            new
            {
                body = markedBody
            },
            cancellationToken);
    }

    /// <summary>
    /// /api/v3 has no route that changes an issue's state (PATCH answers 404), so this posts the "Close" form of the
    /// issue page, signed in on the web side as the token's user (<see cref="GitBucketWeb"/>). The form answers with a
    /// redirect whether it worked or not; only the issue read back from the API says it is closed.
    /// </summary>
    public async Task CloseIssueAsync(string repo, int number, CancellationToken cancellationToken)
    {
        var issue = await GetIssueAsync(repo, number, cancellationToken);
        if (!issue.IsOpen())
        {
            return;
        }

        var closeUrl = $"{repo}/issue_comments/state";
        KeyValuePair<string, string>[] closeForm = [new("issueId", number.ToString()), new("action", "close")];
        var answer = await web.PostSignedInAsync(closeUrl, closeForm, CurrentLoginAsync, cancellationToken);

        var closedIssue = await GetIssueAsync(repo, number, cancellationToken);
        if (closedIssue.IsOpen())
        {
            web.ForgetSession();

            var reason = answer.IsSignInPage
                ? "GitBucket still asks to sign in right after a sign-in it accepted"
                : $"the user of the GitBucket token needs write access to {repo}";
            throw new CodeHostException(
                answer.Status,
                $"POST {closeUrl} -> {(int)answer.Status}: #{number} is still open; {reason}.");
        }
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
        catch (CodeHostException exception) when (exception.Status == HttpStatusCode.NotFound)
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
        catch (CodeHostException exception)
            when (exception.Status is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<HostPull> CreatePullAsync(string repo, NewPull pull, CancellationToken cancellationToken)
    {
        // GitBucket's create reply is not readable as HostPull; only the number is trusted, the PR is read back
        // with GET.
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
        var shortReply = Quote(reply);
        throw new CodeHostException(
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
        Authorize(request);
        using var response = await downloadClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var status = (int)response.StatusCode;
        if (WebAnswer.IsRedirectStatus(response.StatusCode))
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

    /// <summary>The token's user: the web side signs in as the same account the API acts as.</summary>
    private async Task<string> CurrentLoginAsync(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);

        return user.Login;
    }

    /// <summary>Same scheme, host and port as the API: the token is never sent anywhere else.</summary>
    private bool IsOnGitBucket(Uri uri)
    {
        var gitBucket = httpClient.BaseAddress;

        return gitBucket is not null &&
               string.Equals(uri.Scheme, gitBucket.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Authority, gitBucket.Authority, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The current token, read now; no header at all while there is none (setup mode).</summary>
    private void Authorize(HttpRequestMessage request)
    {
        var token = credentials.Current.Token;
        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        }
    }

    /// <summary>A reply quoted in an error message, shortened and without any secret a server might echo.</summary>
    private string Quote(string reply)
    {
        var redacted = credentials.Redact(reply);

        return Text.Short(redacted);
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
        Authorize(request);
        if (body is not null)
        {
            var jsonBody = JsonSerializer.Serialize(body, Json);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CodeHostException(
                response.StatusCode,
                $"{method} {url} -> {(int)response.StatusCode}: {Quote(text)}");
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
            throw new CodeHostException(
                response.StatusCode,
                $"{method} {url}: cannot read the reply as {typeof(T).Name} ({exception.Message}). Reply: {Quote(text)}");
        }
    }
}
