using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Hosting;

/// <summary>
/// --attachment-test URL: downloads one _attached file the way the jobs do and says what GitBucket answered.
/// It costs nothing (no Copilot) and changes nothing on GitBucket; use it to check that the token is accepted
/// for attachments on your server.
/// </summary>
public sealed class AttachmentSelfTest(
    RunnerConfigSource configSource,
    ICodeHost codeHost,
    ILogger<AttachmentSelfTest> logger)
{
    /// <summary>The live config, read per call: each call works with the projects as they are now.</summary>
    private RunnerConfig Config => configSource.Current;

    public async Task RunAsync(string url, CancellationToken cancellationToken)
    {
        var baseUrl = Config.CodeHost.BaseUrl.TrimEnd('/');
        var absoluteUrl = url.StartsWith('/') ? baseUrl + url : url;
        var directory = Directory.CreateTempSubdirectory("ai-runner-attachment-").FullName;
        logger.LogInformation("Downloading {Url} into {Directory}", absoluteUrl, directory);

        var download = new AttachmentDownload(absoluteUrl, directory, "test", Config.Attachments.MaxFileBytes);
        try
        {
            var downloaded = await codeHost.DownloadAttachmentAsync(download, cancellationToken);
            var size = AttachmentReport.Size(downloaded.Bytes);
            var extension = Path.GetExtension(downloaded.Path);
            var kind = AttachmentPolicy.Classify(extension, downloaded.ContentType);
            logger.LogInformation(
                "OK: {File} ({Size}), content type {Type}, server name {Name}, kind {Kind}",
                downloaded.Path,
                size,
                downloaded.ContentType ?? "?",
                downloaded.OriginalName ?? "?",
                kind);
        }
        catch (Exception exception) when (
            exception is AttachmentDownloadException or HttpRequestException or CodeHostException)
        {
            logger.LogError("FAILED: {Reason}", exception.Message);
        }
    }
}
