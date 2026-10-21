using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>A file linked from issue or PR text through GitBucket's <c>_attached</c> route.</summary>
/// <param name="Url">Absolute URL on the GitBucket host.</param>
/// <param name="Id">The part after <c>_attached/</c>.</param>
/// <param name="Name">The link text from the markdown, or the id when the link has none.</param>
/// <param name="IsImage">The text embeds it as an image (<c>![x](...)</c> or an img tag).</param>
public sealed record AttachmentRef(string Url, string Id, string Name, bool IsImage);

/// <summary>
/// Finds attachments in the markdown of an issue, comment or PR. GitBucket's JSON has no attachment list,
/// so the links in the text are all there is. Only links on the GitBucket host count: the runner sends its
/// token there and nowhere else. Pure parsing, so it lives in Core; only the GitBucket provider calls it
/// (ICodeHost.FindAttachments).
/// </summary>
public static class AttachmentLinks
{
    private const string UrlCharacters = @"[^\s)""'<>\]]";
    private const string SegmentCharacters = @"[^\s)""'<>\]/?#]";

    // Absolute or relative (/owner/repo/_attached/id); leftmost match wins, so an absolute URL is never cut.
    // The lookbehind keeps "other.com/x/_attached/1" (no scheme) from being read as the root-relative "/x/_attached/1".
    private static readonly Regex AttachedUrl = new(
        @"(?<![\w.\-/:])(?:https?://[^/\s)""'<>\]]+)?(?:/" + SegmentCharacters + @"+)*/_attached/" +
        SegmentCharacters + "+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // [name](url) and ![name](url)
    private static readonly Regex MarkdownLink = new(
        @"(?<bang>!?)\[(?<name>[^\]]*)\]\((?<url>" + UrlCharacters + "+)",
        RegexOptions.Compiled);

    private static readonly Regex ImageTag = new(
        @"<img\b[^>]*?\bsrc\s*=\s*[""'](?<url>" + UrlCharacters + "+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The distinct attachments of <paramref name="text"/>, in order of appearance.</summary>
    public static IReadOnlyList<AttachmentRef> Find(string? text, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !text.Contains("_attached", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
        {
            return [];
        }

        var names = new Dictionary<string, (string Name, bool IsImage)>();
        foreach (Match link in MarkdownLink.Matches(text))
        {
            var url = Resolve(link.Groups["url"].Value, baseUri);
            if (url is not null)
            {
                var linkName = link.Groups["name"].Value.Trim();
                var isImageSyntax = link.Groups["bang"].Value == "!";
                names.TryAdd(url, (linkName, isImageSyntax));
            }
        }

        var imageUrls = new HashSet<string>();
        foreach (Match tag in ImageTag.Matches(text))
        {
            var url = Resolve(tag.Groups["url"].Value, baseUri);
            if (url is not null)
            {
                imageUrls.Add(url);
            }
        }

        var found = new List<AttachmentRef>();
        var seen = new HashSet<string>();
        foreach (Match match in AttachedUrl.Matches(text))
        {
            var url = Resolve(match.Value, baseUri);
            if (url is null || !seen.Add(url))
            {
                continue;
            }

            var id = IdOf(url);
            var (name, isImage) = names.TryGetValue(url, out var known) ? known : ("", false);
            var shownName = string.IsNullOrWhiteSpace(name) ? id : name;
            var isEmbedded = isImage || imageUrls.Contains(url);
            found.Add(new AttachmentRef(url, id, shownName, isEmbedded));
        }

        return found;
    }

    /// <summary>The absolute URL when it is an _attached link on the base host, else null.</summary>
    private static string? Resolve(string raw, Uri baseUri)
    {
        var trimmed = raw.TrimEnd('.', ',', ';', ':', '!');
        var isLinkShape = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                          trimmed.StartsWith('/');
        if (!isLinkShape || !trimmed.Contains("/_attached/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUri, trimmed, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var isSameHost = string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
                         uri.Port == baseUri.Port;

        return isSameHost ? uri.GetLeftPart(UriPartial.Path) : null;
    }

    private static string IdOf(string url)
    {
        var index = url.LastIndexOf("/_attached/", StringComparison.OrdinalIgnoreCase);

        return url[(index + "/_attached/".Length)..];
    }
}
