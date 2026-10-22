using System.Net;

namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// The web pages of GitBucket, for what /api/v3 cannot do: it has no "edit an issue" route, so an issue cannot be
/// closed through it. Web routes ignore the API token, so this signs in with the password and keeps the session
/// cookie. The sign-in and its quirks live here only; <see cref="GitBucketCodeHost"/> just posts a form.
/// Build it with <see cref="Create"/>; tests pass their own client.
/// </summary>
/// <param name="credentials">
/// The web password is read at each sign-in (Setup page, else code_host.web_password). Empty = not set: posting a
/// form then fails.
/// </param>
public sealed class GitBucketWeb(HttpClient client, RunnerCredentials credentials) : IDisposable
{
    /// <summary>
    /// The credentials version the session cookie was got with, or null before a sign-in. A new web password or a new
    /// token (maybe of another user) changes the version, so the next form signs in again. No lock: only jobs post
    /// forms and the worker runs one job at a time; a race would only sign in twice.
    /// </summary>
    private int? _signedInVersion;

    /// <summary>
    /// The client the web side needs: its own cookies (the session), no redirects (a redirect to the sign-in page is
    /// how GitBucket says "not signed in", and the password is never sent on to another host) and the GitBucket root
    /// as base address, so a server under a context path (http://host/gitbucket) works.
    /// </summary>
    public static GitBucketWeb Create(string baseUrl, RunnerCredentials credentials)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        var rootUrl = baseUrl.TrimEnd('/') + "/";
        var webClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(rootUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

        return new GitBucketWeb(webClient, credentials);
    }

    /// <summary>
    /// Posts a web form with the session, signed in as the user <paramref name="loginOf"/> names (read only when a
    /// sign-in is needed). Signs in first, and once more when the session has ended (GitBucket restarted, or the
    /// session timed out), which GitBucket shows as a redirect to the sign-in page.
    /// </summary>
    public async Task<WebAnswer> PostSignedInAsync(
        string url,
        KeyValuePair<string, string>[] form,
        Func<CancellationToken, Task<string>> loginOf,
        CancellationToken cancellationToken)
    {
        if (!credentials.Current.IsWebPasswordSet)
        {
            throw new CodeHostException(
                HttpStatusCode.Unauthorized,
                "GitBucket /api/v3 cannot close an issue, so the runner uses the web page, which needs the password " +
                "of the token's user: set it on the dashboard's Setup page (or code_host.web_password in repos.yml).");
        }

        if (_signedInVersion != credentials.Version)
        {
            await SignInAsync(loginOf, cancellationToken);
        }

        var answer = await PostFormAsync(url, form, cancellationToken);
        if (!answer.IsSignInPage)
        {
            return answer;
        }

        _signedInVersion = null;
        await SignInAsync(loginOf, cancellationToken);

        return await PostFormAsync(url, form, cancellationToken);
    }

    /// <summary>
    /// For a form that did not do its work although the answer looked fine: the session may have ended without a
    /// redirect to the sign-in page, so the next form signs in again.
    /// </summary>
    public void ForgetSession() => _signedInVersion = null;

    /// <summary>The runner's web side lives as long as the runner; a check disposes its own after use.</summary>
    public void Dispose() => client.Dispose();

    /// <summary>
    /// Signs in as <paramref name="login"/>: the Setup page's check of a new web password, on a web client built for
    /// the check. Throws <see cref="CodeHostException"/> (401) when GitBucket refuses it.
    /// </summary>
    public Task CheckSignInAsync(string login, CancellationToken cancellationToken) =>
        SignInAsync(_ => Task.FromResult(login), cancellationToken);

    private async Task SignInAsync(Func<CancellationToken, Task<string>> loginOf, CancellationToken cancellationToken)
    {
        var login = await loginOf(cancellationToken);
        var version = credentials.Version;
        var password = credentials.Current.WebPassword;
        KeyValuePair<string, string>[] signInForm = [new("userName", login), new("password", password)];
        var answer = await PostFormAsync("signin", signInForm, cancellationToken);

        // A wrong password is a redirect back to /signin; a right one is a redirect anywhere else.
        if (!answer.IsRedirect || answer.IsSignInPage)
        {
            throw new CodeHostException(
                HttpStatusCode.Unauthorized,
                $"POST signin -> {(int)answer.Status}: GitBucket did not accept the web sign-in of {login}. " +
                "Check the web password on the dashboard's Setup page (or code_host.web_password in repos.yml): " +
                "the password that account uses on the sign-in page.");
        }

        _signedInVersion = version;
    }

    private async Task<WebAnswer> PostFormAsync(
        string url,
        KeyValuePair<string, string>[] form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(url, content, cancellationToken);

        return new WebAnswer(response.StatusCode, response.Headers.Location);
    }
}

/// <summary>
/// What a web form answered. GitBucket answers a form with a redirect, to the sign-in page when not signed in.
/// </summary>
public sealed record WebAnswer(HttpStatusCode Status, Uri? Location)
{
    public bool IsRedirect => IsRedirectStatus(Status);

    /// <summary>The Location path ends in /signin, relative or absolute, under any context path.</summary>
    public bool IsSignInPage =>
        Status == HttpStatusCode.Unauthorized ||
        (IsRedirect &&
         Location is not null &&
         PathOf(Location).EndsWith("/signin", StringComparison.OrdinalIgnoreCase));

    public static bool IsRedirectStatus(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private static string PathOf(Uri location)
    {
        var text = location.OriginalString;
        var queryStart = text.IndexOfAny(['?', '#']);
        var path = queryStart < 0 ? text : text[..queryStart];

        return "/" + path.TrimEnd('/');
    }
}
