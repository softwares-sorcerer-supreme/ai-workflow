using AiRunner.Core;

namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// The code host as the jobs use it: issues, comments, labels, pull requests, closing, attachments
/// (docs/adr/0004). "repo" is always "owner/name". One provider per kind of host (today only
/// <see cref="GitBucketCodeHost"/>), so a host quirk is fixed in one class; what the jobs must know about the host
/// is in <see cref="Capabilities"/>.
/// </summary>
public interface ICodeHost
{
    CodeHostCapabilities Capabilities { get; }

    /// <summary>The files linked from issue, comment or PR text that this host serves as attachments.</summary>
    IReadOnlyList<AttachmentRef> FindAttachments(string? text);

    Task<HostUser> GetCurrentUserAsync(CancellationToken cancellationToken);

    Task<HostRepo> GetRepoAsync(string repo, CancellationToken cancellationToken);

    /// <summary>Open issues (no PRs), all pages. Null filters are not sent.</summary>
    Task<IReadOnlyList<HostIssue>> ListOpenIssuesAsync(
        string repo,
        string? label,
        string? assignee,
        CancellationToken cancellationToken);

    /// <summary>Closed issues (no PRs) with the label, all pages.</summary>
    Task<IReadOnlyList<HostIssue>> ListClosedIssuesAsync(
        string repo,
        string label,
        CancellationToken cancellationToken);

    Task<HostIssue> GetIssueAsync(string repo, int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<HostComment>> ListCommentsAsync(string repo, int number, CancellationToken cancellationToken);

    Task<HostIssue> CreateIssueAsync(string repo, NewIssue issue, CancellationToken cancellationToken);

    Task<HostComment> AddCommentAsync(string repo, int number, string body, CancellationToken cancellationToken);

    /// <summary>
    /// A comment written as the token's user, without the runner's marker: the planner takes it as a person's comment
    /// (a dashboard action that stands for the user typing "@ai ..." on the code host). Text only, no runner blocks.
    /// </summary>
    Task<HostComment> AddPersonCommentAsync(string repo, int number, string body, CancellationToken cancellationToken);

    Task<HostComment> UpdateCommentAsync(string repo, long commentId, string body, CancellationToken cancellationToken);

    Task AddLabelsAsync(string repo, int number, IEnumerable<string> labels, CancellationToken cancellationToken);

    /// <summary>
    /// Closes an issue; nothing happens when it is closed already. Throws when it is still open afterwards.
    /// </summary>
    Task CloseIssueAsync(string repo, int number, CancellationToken cancellationToken);

    /// <summary>No error when the label is not on the issue.</summary>
    Task RemoveLabelAsync(string repo, int number, string label, CancellationToken cancellationToken);

    /// <summary>Creates a repo label; returns false when it already exists.</summary>
    Task<bool> CreateLabelAsync(string repo, NewLabel label, CancellationToken cancellationToken);

    /// <summary>state = open | closed | all.</summary>
    Task<IReadOnlyList<HostPull>> ListPullsAsync(string repo, string state, CancellationToken cancellationToken);

    Task<HostPull> GetPullAsync(string repo, int number, CancellationToken cancellationToken);

    Task<HostPull> CreatePullAsync(string repo, NewPull pull, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads one _attached file into <see cref="AttachmentDownload.Directory"/>. Throws
    /// <see cref="AttachmentDownloadException"/> with a short reason when it cannot be had.
    /// </summary>
    Task<DownloadedAttachment> DownloadAttachmentAsync(AttachmentDownload request, CancellationToken cancellationToken);
}

/// <param name="Url">Absolute URL as <see cref="ICodeHost.FindAttachments"/> found it; must be on the host.</param>
/// <param name="Directory">Where the file goes.</param>
/// <param name="FileStem">The file name without extension; the extension is chosen from what the server says.</param>
/// <param name="MaxBytes">Larger files are refused (and never kept).</param>
public sealed record AttachmentDownload(string Url, string Directory, string FileStem, long MaxBytes);

/// <param name="Path">The saved file.</param>
/// <param name="OriginalName">The name the server gave (Content-Disposition), or null.</param>
public sealed record DownloadedAttachment(string Path, string? OriginalName, string? ContentType, long Bytes);

/// <summary>An attachment that cannot be downloaded; the message is a short reason for the context file.</summary>
public sealed class AttachmentDownloadException(string reason) : Exception(reason);
