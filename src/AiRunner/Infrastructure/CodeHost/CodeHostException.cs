using System.Net;

namespace AiRunner.Infrastructure.CodeHost;

public sealed class CodeHostException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
