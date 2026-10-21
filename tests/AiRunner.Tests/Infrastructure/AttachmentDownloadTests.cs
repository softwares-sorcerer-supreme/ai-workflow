using System.Net;
using System.Net.Http.Headers;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Tests.Infrastructure;

/// <summary>GitBucketCodeHost.DownloadAttachmentAsync against canned HTTP answers: host pin, redirects, size cap.</summary>
public sealed class AttachmentDownloadTests : IDisposable
{
    private const string Url = "http://gb/james/demo/_attached/abc123";
    private const string Token = "t0ken";

    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-download-").FullName;

    public void Dispose() => FileSystem.DeleteDirectory(_folder);

    /// <summary>A body whose length is not announced, like a chunked answer.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    private static HttpResponseMessage Ok(byte[] bytes, string contentType, string? fileName = null)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (fileName is not null)
        {
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static (GitBucketCodeHost Client, StubHttpHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string apiUrl = "http://gb/api/v3/")
    {
        var handler = new StubHttpHandler(respond);
        var apiHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var apiClient = new HttpClient(apiHandler) { BaseAddress = new Uri(apiUrl) };
        var downloadClient = new HttpClient(handler);
        var credentials = RunnerCredentials.Of(token: Token);
        var web = new GitBucketWeb(new HttpClient(apiHandler), credentials);

        return (new GitBucketCodeHost(apiClient, downloadClient, web, credentials, "http://gb"), handler);
    }

    private AttachmentDownload Download(long maxBytes = 1000, string url = Url) =>
        new(url, Path.Combine(_folder, "attachments"), "1-abc123", maxBytes);

    [Fact]
    public async Task Saves_the_file_with_the_extension_the_server_names()
    {
        var (client, handler) = Create(_ => Ok([1, 2, 3], "application/octet-stream", "\"Shot.PNG\""));
        var download = Download();

        var downloaded = await client.DownloadAttachmentAsync(download, default);

        Assert.Equal("1-abc123.png", Path.GetFileName(downloaded.Path));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(downloaded.Path));
        Assert.Equal(3, downloaded.Bytes);
        Assert.Equal("Shot.PNG", downloaded.OriginalName);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Extension_comes_from_the_content_type_when_the_server_gives_no_name()
    {
        var (client, _) = Create(_ => Ok([1], "image/png"));
        var download = Download();

        var downloaded = await client.DownloadAttachmentAsync(download, default);

        Assert.EndsWith(".png", downloaded.Path);
    }

    [Fact]
    public async Task A_link_on_another_host_is_refused_without_any_request()
    {
        var (client, handler) = Create(_ => Ok([1], "image/png"));
        var foreign = Download(url: "http://evil.test/james/demo/_attached/x");

        var exception = await Assert.ThrowsAsync<AttachmentDownloadException>(
            () => client.DownloadAttachmentAsync(foreign, default));

        Assert.Contains("not on the GitBucket host", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_link_with_another_scheme_is_refused_so_the_token_is_never_sent_in_clear()
    {
        var (client, handler) = Create(_ => Ok([1], "image/png"), apiUrl: "https://gb/api/v3/");
        var plain = Download(url: "http://gb/james/demo/_attached/x");

        var exception = await Assert.ThrowsAsync<AttachmentDownloadException>(
            () => client.DownloadAttachmentAsync(plain, default));

        Assert.Contains("not on the GitBucket host", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_redirect_is_reported_and_not_followed()
    {
        var (client, _) = Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://gb/signin");

            return response;
        });
        var download = Download();

        var exception = await Assert.ThrowsAsync<AttachmentDownloadException>(
            () => client.DownloadAttachmentAsync(download, default));

        Assert.Contains("redirected to http://gb/signin", exception.Message);
        Assert.Contains("token", exception.Message);
    }

    [Fact]
    public async Task An_error_status_is_a_short_reason()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var download = Download();

        var exception = await Assert.ThrowsAsync<AttachmentDownloadException>(
            () => client.DownloadAttachmentAsync(download, default));

        Assert.Contains("404", exception.Message);
    }

    [Fact]
    public async Task A_file_announced_as_too_large_is_never_written()
    {
        var (client, _) = Create(_ => Ok(new byte[50], "image/png"));
        var small = Download(maxBytes: 10);

        var exception = await Assert.ThrowsAsync<AttachmentDownloadException>(
            () => client.DownloadAttachmentAsync(small, default));

        Assert.Contains("too large", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(_folder, "attachments")));
    }

    [Fact]
    public async Task A_file_that_grows_past_the_cap_while_reading_is_deleted()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[50]),
        });
        var small = Download(maxBytes: 10);

        var exception = await Assert.ThrowsAsync<AttachmentDownloadException>(
            () => client.DownloadAttachmentAsync(small, default));

        Assert.Contains("too large", exception.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "attachments")));
    }
}
