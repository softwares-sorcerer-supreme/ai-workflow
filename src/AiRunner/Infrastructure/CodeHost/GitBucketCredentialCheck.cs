namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// The Setup page's checks on GitBucket: GET /user with the new token, and the web sign-in with the new password.
/// </summary>
/// <param name="codeHostFor">A code host that sends the given token; a throwaway one per check.</param>
/// <param name="webFor">A new web client with its own cookies that signs in with the password; disposed after.</param>
public sealed class GitBucketCredentialCheck(
    Func<string, ICodeHost> codeHostFor,
    Func<string, GitBucketWeb> webFor) : ICredentialCheck
{
    public async Task<CredentialCheck> CheckTokenAsync(string token, CancellationToken cancellationToken)
    {
        var codeHost = codeHostFor(token);
        try
        {
            var user = await codeHost.GetCurrentUserAsync(cancellationToken);

            return new CredentialCheck(CheckVerdict.Accepted, user.Login);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return CredentialCheck.FromFailure(exception);
        }
    }

    public async Task<CredentialCheck> CheckWebPasswordAsync(
        string login,
        string password,
        CancellationToken cancellationToken)
    {
        using var web = webFor(password);
        try
        {
            await web.CheckSignInAsync(login, cancellationToken);

            return new CredentialCheck(CheckVerdict.Accepted, login);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return CredentialCheck.FromFailure(exception);
        }
    }
}
