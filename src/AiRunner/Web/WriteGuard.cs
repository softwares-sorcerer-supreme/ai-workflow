using System.Net;
using Microsoft.AspNetCore.Http;

namespace AiRunner.Web;

/// <summary>Endpoint metadata that marks a route as a write; only these routes take a method other than GET.</summary>
public sealed class WriteRoute
{
    public static WriteRoute Instance { get; } = new();
}

/// <summary>
/// The CSRF guard of every write route (docs/adr/0004). Any web page the user opens can send a request to
/// 127.0.0.1, so a write must prove it comes from the dashboard itself:
/// - the header <c>X-AiRunner-Request: 1</c>: a cross-site form cannot set it, and a cross-site <c>fetch</c> with it
///   needs a CORS preflight the runner never answers;
/// - <c>Content-Type: application/json</c>: rules out the form and text bodies a page can post without a preflight;
/// - an <c>Origin</c>, when the browser sends one, equal to the dashboard's own origin;
/// - a loopback caller: writes exist only while the dashboard is local (it has no login).
/// The Host filter runs before it. It runs as a middleware after routing, not as an endpoint filter, because endpoint
/// filters run after parameter binding: a form post to a route with a JSON body would get a 415 before the guard.
/// </summary>
public static class WriteGuard
{
    public const string HeaderName = "X-AiRunner-Request";

    private const string HeaderValue = "1";
    private const string JsonMediaType = "application/json";

    /// <summary>Why the write is refused, or null when it may go on.</summary>
    public static string? WhyRefused(HttpRequest request, int port)
    {
        if (request.Headers[HeaderName] != HeaderValue)
        {
            return $"A write needs the header {HeaderName}: {HeaderValue}.";
        }

        if (!IsJson(request.ContentType))
        {
            return $"A write must be sent as {JsonMediaType}.";
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsOwnOrigin(origin, port))
        {
            return "A write must come from the dashboard's own page.";
        }

        var remote = request.HttpContext.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            return "Writes are only accepted from this computer.";
        }

        return null;
    }

    private static bool IsJson(string? contentType)
    {
        if (contentType is null)
        {
            return false;
        }

        var mediaType = contentType.Split(';')[0].Trim();

        return string.Equals(mediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOwnOrigin(string origin, int port) =>
        string.Equals(origin, $"http://localhost:{port}", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(origin, $"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase);
}
