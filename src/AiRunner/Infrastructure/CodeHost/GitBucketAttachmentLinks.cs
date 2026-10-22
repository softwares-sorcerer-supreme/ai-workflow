using System.Text.RegularExpressions;
using AiRunner.Core;

namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// Finds attachments in the markdown of an issue, comment or PR. GitBucket's JSON has no attachment list,
/// so the links in the text are all there is. Only links on the GitBucket host count: the runner sends its
/// token there and nowhere else. Pure parsing, but of GitBucket's route, so it lives with the provider (ADR 0004)
/// and is reached through ICodeHost.FindAttachments.
/// </summary>
public static class GitBucketAttachmentLinks
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
