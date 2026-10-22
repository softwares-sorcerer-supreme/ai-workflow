using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Jobs;

/// <summary>The texts of one job to scan for attachments, and the clone they are saved in.</summary>
public sealed record AttachmentRequest(string Directory, IReadOnlyList<AttachmentSource> Sources)
{
    /// <summary>The usual input: the issue or PR body, then the human comments.</summary>
    public static AttachmentRequest Of(string directory, AttachmentSource body, IEnumerable<HostComment> comments) =>
        new(directory, AttachmentSource.Of(body, comments));
}

/// <summary>What the fetcher did: the section for the context file and the files for the Copilot prompt.</summary>
public sealed record AttachmentSet(IReadOnlyList<AttachmentResult> Results)
{
    public static readonly AttachmentSet Empty = new([]);

    /// <summary>The "Attachments" section to append to context.md or plan.md; "" when there are none.</summary>
    public string Section => AttachmentReport.Render(Results);

    /// <summary>Absolute paths for the --attachment flag.</summary>
    public IReadOnlyList<string> ForCopilot =>
        Results
            .Where(result => result.PassToCopilot && result.FullPath is not null)
            .Select(result => result.FullPath!)
            .ToList();
}

/// <summary>
/// Downloads the files people attached to an issue, comment or PR into <c>.ai-workflow/attachments/</c> of the
/// clone, so the agent can read screenshots, logs and documents. A file that cannot be had never fails the
/// job: it becomes a line in the Attachments section that says why. Call it after the workspace is prepared,
/// since the next git clean removes the folder.
/// </summary>
public sealed class AttachmentFetcher(
    RunnerConfig config,
    ICodeHost codeHost,
    ICopilotRunner copilot)
{
    private static readonly string[] FolderSegments = [".ai-workflow", "attachments"];

    /// <summary>Where the files are, relative to the clone, as the agent reads it.</summary>
    private static readonly string Folder = string.Join('/', FolderSegments);

    /// <summary>What is left of the limits while a job's attachments are fetched.</summary>
    private sealed class Budget
    {
        /// <summary>Downloads tried, failed ones included: a post full of dead links must not cost a request each.</summary>
        public int Tried { get; set; }

        public int Passed { get; set; }

        public long Bytes { get; set; }
    }

    /// <summary>One link on its way to the clone.</summary>
    private sealed record Candidate(AttachmentRef Reference, string Source, string Folder, JobRun Run)
    {
        public int Number { get; init; }

        public bool CanPass { get; init; }
    }

    public async Task<AttachmentSet> FetchAsync(
        AttachmentRequest request,
        JobRun run,
        CancellationToken cancellationToken)
    {
        var settings = config.Attachments;
        if (!settings.Enabled)
        {
            return AttachmentSet.Empty;
        }

        var found = FindAll(request.Sources);
        if (found.Count == 0)
        {
            return AttachmentSet.Empty;
        }

        var canPass = settings.PassToCopilot &&
                      await copilot.CanAttachFilesAsync(request.Directory, cancellationToken);
        if (settings.PassToCopilot && !canPass)
        {
            run.Info("the installed Copilot CLI has no --attachment flag: attachments are saved and listed, not attached");
        }

        var results = new List<AttachmentResult>();
        var budget = new Budget();
        var folder = Path.Combine([request.Directory, .. FolderSegments]);

        foreach (var (reference, source) in found)
        {
            var candidate = new Candidate(reference, source, folder, run)
            {
                Number = results.Count + 1,
                CanPass = canPass,
            };
            var result = await FetchOneAsync(candidate, budget, cancellationToken);
            results.Add(result);
        }

        return new AttachmentSet(results);
    }

    private async Task<AttachmentResult> FetchOneAsync(
        Candidate candidate,
        Budget budget,
        CancellationToken cancellationToken)
    {
        var settings = config.Attachments;
        var (reference, source, folder, run) = candidate;

        var refusal = RefusalBeforeDownload(reference, budget);
        if (refusal is not null)
        {
            return Unavailable(candidate, refusal);
        }

        budget.Tried++;
        var maxBytes = Math.Min(settings.MaxFileBytes, settings.MaxTotalBytes - budget.Bytes);
        var stem = $"{candidate.Number}-{SafeStem(reference.Id)}";
        var download = new AttachmentDownload(reference.Url, folder, stem, maxBytes);
        var (downloaded, failure) = await TryDownloadAsync(download, cancellationToken);
        if (downloaded is null)
        {
            return Unavailable(candidate, failure!);
        }

        var extension = Path.GetExtension(downloaded.Path);
        if (AttachmentPolicy.IsSkippedExtension(extension, settings.SkipExtensions))
        {
            FileSystem.DeleteFile(downloaded.Path);

            return Unavailable(candidate, NeverFetched(extension));
        }

        var classified = AttachmentPolicy.Classify(extension, downloaded.ContentType);
        var isEmbeddedImage = classified == AttachmentKind.Other && reference.IsImage;
        var kind = isEmbeddedImage ? AttachmentKind.Image : classified;
        var shouldPass = candidate.CanPass &&
                         AttachmentPolicy.CanPassToCopilot(kind) &&
                         budget.Passed < settings.MaxPassedToCopilot;
        budget.Bytes += downloaded.Bytes;
        if (shouldPass)
        {
            budget.Passed++;
        }

        var fileName = Path.GetFileName(downloaded.Path);
        run.Info($"attachment {reference.Id}: {fileName}, {AttachmentReport.Size(downloaded.Bytes)}, {kind}");

        return new AttachmentResult(
            Source: source,
            RelativePath: $"{Folder}/{fileName}",
            FullPath: downloaded.Path,
            Name: ShownName(reference, downloaded),
            Bytes: downloaded.Bytes,
            Kind: kind,
            PassToCopilot: shouldPass,
            Problem: null);
    }

    /// <summary>Every attachment of every source once, with the first source that linked it.</summary>
    private List<(AttachmentRef Reference, string Source)> FindAll(IReadOnlyList<AttachmentSource> sources)
    {
        var found = new List<(AttachmentRef, string)>();
        var seen = new HashSet<string>();
        foreach (var source in sources)
        {
            foreach (var reference in codeHost.FindAttachments(source.Text))
            {
                if (seen.Add(reference.Url))
                {
                    found.Add((reference, source.Label));
                }
            }
        }

        return found;
    }

    /// <summary>Why a file is not even tried: a limit is reached or its link text says it is an executable.</summary>
    private string? RefusalBeforeDownload(AttachmentRef reference, Budget budget)
    {
        var settings = config.Attachments;
        if (budget.Tried >= settings.MaxFiles)
        {
            return $"more than {settings.MaxFiles} attachments, the rest are not fetched";
        }

        if (budget.Bytes >= settings.MaxTotalBytes)
        {
            return $"the total size limit of {AttachmentReport.Size(settings.MaxTotalBytes)} is reached";
        }

        // The link text and the id can both name the file ("[log](.../_attached/abc.exe)").
        string[] extensions = [AttachmentPolicy.SafeExtension(reference.Name), AttachmentPolicy.SafeExtension(reference.Id)];
        foreach (var extension in extensions)
        {
            if (AttachmentPolicy.IsSkippedExtension(extension, settings.SkipExtensions))
            {
                return NeverFetched(extension);
            }
        }

        return null;
    }

    private static string NeverFetched(string extension) => $"{extension} files are never fetched";

    private async Task<(DownloadedAttachment? Downloaded, string? Failure)> TryDownloadAsync(
        AttachmentDownload download,
        CancellationToken cancellationToken)
    {
        try
        {
            var downloaded = await codeHost.DownloadAttachmentAsync(download, cancellationToken);

            return (downloaded, null);
        }
        catch (AttachmentDownloadException exception)
        {
            return (null, exception.Message);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException or CodeHostException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, $"download failed ({Text.Short(exception.Message, 120)})");
        }
    }

    /// <summary>A file the agent will not get; the reason goes into the Attachments section and the job log.</summary>
    private static AttachmentResult Unavailable(Candidate candidate, string problem)
    {
        var (reference, source, _, run) = candidate;
        run.Info($"attachment {reference.Id}: not fetched - {problem}");

        return new AttachmentResult(
            Source: source,
            RelativePath: null,
            FullPath: null,
            Name: reference.Name,
            Bytes: 0,
            Kind: AttachmentKind.Other,
            PassToCopilot: false,
            Problem: problem);
    }

    /// <summary>The name people gave the link, else the name the server sent, else the id.</summary>
    private static string ShownName(AttachmentRef reference, DownloadedAttachment downloaded)
    {
        var hasLinkName = !string.Equals(reference.Name, reference.Id, StringComparison.Ordinal);

        return hasLinkName || string.IsNullOrWhiteSpace(downloaded.OriginalName)
            ? reference.Name
            : downloaded.OriginalName;
    }

    /// <summary>The id from the server becomes part of a file name: letters, digits, '-' and '_' only.</summary>
    private static string SafeStem(string id)
    {
        var characters = id.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
        var stem = new string(characters.Take(40).ToArray());

        return stem.Length == 0 ? "file" : stem;
    }
}
