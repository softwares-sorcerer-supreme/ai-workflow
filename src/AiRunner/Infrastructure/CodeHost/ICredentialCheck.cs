using System.Net;

namespace AiRunner.Infrastructure.CodeHost;

/// <summary>How the code host answered a check of a new secret.</summary>
public enum CheckVerdict
{
    Accepted,

    /// <summary>The host answered 401 or 403.</summary>
    NotAccepted,

    /// <summary>No answer at all: wrong base_url, no VPN, host down, timeout.</summary>
    Unreachable,

    /// <summary>
    /// The host answered with another error (<see cref="CredentialCheck.Status"/>), or the check broke in another way
    /// (no status).
    /// </summary>
    Failed,
}

/// <summary>The answer of one check. <c>Login</c> is the token's user, set only on an accepted token.</summary>
public sealed record CredentialCheck(CheckVerdict Verdict, string Login = "", int? Status = null)
{
    public bool IsAccepted => Verdict == CheckVerdict.Accepted;

    /// <summary>
    /// Turns any failed call into a verdict. Only the status is kept, never the exception's text: it may quote what
    /// the server answered, and a check must never put a secret into a log or an answer.
    /// </summary>
    public static CredentialCheck FromFailure(Exception exception) => exception switch
    {
        CodeHostException { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            new CredentialCheck(CheckVerdict.NotAccepted),
        CodeHostException codeHostException =>
            new CredentialCheck(CheckVerdict.Failed, Status: (int)codeHostException.Status),
        HttpRequestException or TaskCanceledException => new CredentialCheck(CheckVerdict.Unreachable),
        _ => new CredentialCheck(CheckVerdict.Failed),
    };
}

/// <summary>
/// Checks a secret against the code host before the Setup page saves it (docs/specs/dashboard-control.md section 3).
/// Each check runs on clients built for it, so the runner's own clients keep the old secret until the new one is
/// accepted.
/// </summary>
public interface ICredentialCheck
{
    /// <summary>Asks the host who the token belongs to.</summary>
    Task<CredentialCheck> CheckTokenAsync(string token, CancellationToken cancellationToken);

    /// <summary>Signs in on the host's web side as <paramref name="login"/> with the password.</summary>
    Task<CredentialCheck> CheckWebPasswordAsync(string login, string password, CancellationToken cancellationToken);
}
