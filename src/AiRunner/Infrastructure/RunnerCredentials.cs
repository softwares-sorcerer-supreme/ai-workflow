namespace AiRunner.Infrastructure;

/// <summary>Where a secret came from, as the dashboard shows it.</summary>
public static class CredentialSource
{
    public const string UserSecrets = "user-secrets";
    public const string Environment = "environment";
    public const string ReposYml = "repos-yml";
    public const string None = "none";
}

/// <summary>
/// The secrets the runner talks to the code host with, at one moment.
/// </summary>
/// <param name="Token">The API token; empty = none yet (setup mode).</param>
/// <param name="GitSecret">The password git sends over HTTP: GitBucket:GitPassword, else the token.</param>
/// <param name="WebPassword">The sign-in password of the token's user, for the web forms; empty = not set.</param>
public sealed record CredentialValues(
    string Token,
    string TokenSource,
    string GitSecret,
    string WebPassword,
    string WebPasswordSource)
{
    public static CredentialValues Empty { get; } =
        new("", CredentialSource.None, "", "", CredentialSource.None);

    public bool IsTokenSet => Token.Length > 0;

    public bool IsWebPasswordSet => WebPassword.Length > 0;
}

/// <summary>
/// The live secrets of the runner (ADR 0004): every client reads them per request, so a token saved on the Setup page
/// is used from the next request on, without a restart. A request already sent keeps the old one. The version goes up
/// with every change, so the web session knows to sign in again. Never handed to the dashboard web app.
/// </summary>
public sealed class RunnerCredentials
{
    private const string Hidden = "***";

    private readonly Lock _lock = new();
    private volatile CredentialValues _values;
    private volatile int _version;
    private TaskCompletionSource _changed = NewSignal();

    public RunnerCredentials(CredentialValues values)
    {
        _values = values;
    }

    /// <summary>Fixed secrets for a client built for one check, or for a test.</summary>
    public static RunnerCredentials Of(string token = "", string webPassword = "")
    {
        var tokenSource = token.Length > 0 ? CredentialSource.UserSecrets : CredentialSource.None;
        var webPasswordSource = webPassword.Length > 0 ? CredentialSource.UserSecrets : CredentialSource.None;
        var values = new CredentialValues(token, tokenSource, token, webPassword, webPasswordSource);

        return new RunnerCredentials(values);
    }

    /// <summary>An immutable record behind a volatile field: every request reads it without taking the lock.</summary>
    public CredentialValues Current => _values;

    public int Version => _version;

    public bool IsTokenSet => Current.IsTokenSet;

    /// <summary>Swaps in new secrets and wakes whoever waits for a token.</summary>
    public void Apply(CredentialValues values)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            _values = values;
            _version++;
            changed = _changed;
            _changed = NewSignal();
        }

        changed.SetResult();
    }

    /// <summary>Returns at once when a token is set, else when one is applied (setup mode ends).</summary>
    public async Task WaitForTokenAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (_values.IsTokenSet)
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The text with every current secret replaced by "***": for a message that quotes what a server answered, in
    /// case the server echoed a secret.
    /// </summary>
    public string Redact(string text)
    {
        var values = Current;
        string[] secrets = [values.Token, values.GitSecret, values.WebPassword];
        var redacted = text;
        foreach (var secret in secrets.Where(secret => secret.Length > 0).OrderByDescending(secret => secret.Length))
        {
            redacted = redacted.Replace(secret, Hidden, StringComparison.Ordinal);
        }

        return redacted;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
