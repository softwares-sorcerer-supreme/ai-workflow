namespace AiRunner.Core;

public enum AttachmentKind
{
    Image,
    Pdf,
    Text,
    Other,
}

/// <summary>
/// What kind of file an attachment is and whether the runner may fetch it. Decisions only: the download
/// itself is Infrastructure. The extension is the runner's own, never a path taken from the server.
/// </summary>
public static class AttachmentPolicy
{
    private static readonly HashSet<string> ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    private static readonly HashSet<string> TextExtensions =
    [
        ".txt", ".md", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".html", ".htm", ".svg", ".sql",
        ".ini", ".cfg", ".conf", ".toml", ".properties", ".diff", ".patch", ".har",
    ];

    private static readonly Dictionary<string, string> ExtensionByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/bmp"] = ".bmp",
        ["application/pdf"] = ".pdf",
        ["text/plain"] = ".txt",
        ["text/csv"] = ".csv",
        ["text/markdown"] = ".md",
        ["text/html"] = ".html",
        ["application/json"] = ".json",
        ["application/xml"] = ".xml",
        ["text/xml"] = ".xml",
    };

    /// <summary>".png" for "Shot.PNG"; "" when the name has no plain extension.</summary>
    public static string SafeExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        var isPlain = extension.Length is > 1 and <= 9 && extension.Skip(1).All(char.IsAsciiLetterOrDigit);

        return isPlain ? extension : "";
    }

    /// <summary>
    /// The extension of the saved file: the name the server sent, else the end of the URL, else what the content
    /// type stands for, else "". Always plain (letters and digits), so it is safe in a file name.
    /// </summary>
    public static string ChooseExtension(string? serverFileName, string urlPath, string? contentType)
    {
        var fromName = SafeExtension(serverFileName);
        if (fromName.Length > 0)
        {
            return fromName;
        }

        var fromUrl = SafeExtension(urlPath);
        if (fromUrl.Length > 0)
        {
            return fromUrl;
        }

        return ExtensionByContentType.GetValueOrDefault(MediaType(contentType), "");
    }

    private static string MediaType(string? contentType) =>
        (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();

    public static AttachmentKind Classify(string extension, string? contentType)
    {
        if (ImageExtensions.Contains(extension))
        {
            return AttachmentKind.Image;
        }

        if (extension == ".pdf")
        {
            return AttachmentKind.Pdf;
        }

        if (TextExtensions.Contains(extension))
        {
            return AttachmentKind.Text;
        }

        var mediaType = MediaType(contentType);

        return mediaType switch
        {
            "image/svg+xml" => AttachmentKind.Text,
            _ when mediaType.StartsWith("image/", StringComparison.Ordinal) => AttachmentKind.Image,
            "application/pdf" => AttachmentKind.Pdf,
            _ when mediaType.StartsWith("text/", StringComparison.Ordinal) => AttachmentKind.Text,
            "application/json" or "application/xml" => AttachmentKind.Text,
            _ => AttachmentKind.Other,
        };
    }

    /// <summary>Executables and scripts are never fetched: nobody should open them from a ticket.</summary>
    public static bool IsSkippedExtension(string extension, IEnumerable<string> skipExtensions) =>
        extension.Length > 0 &&
        skipExtensions.Any(skipped => string.Equals(
            skipped.Trim().TrimStart('.'), extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Images and PDFs can be attached to the Copilot prompt; the rest is read from disk.</summary>
    public static bool CanPassToCopilot(AttachmentKind kind) => kind is AttachmentKind.Image or AttachmentKind.Pdf;

    public static string Describe(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => "image",
        AttachmentKind.Pdf => "pdf",
        AttachmentKind.Text => "text",
        _ => "binary, may not be readable",
    };
}
