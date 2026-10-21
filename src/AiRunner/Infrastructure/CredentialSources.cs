namespace AiRunner.Infrastructure;

/// <summary>
/// The secrets the process environment brings, read once at startup (the environment does not change while the
/// runner runs). <c>GitBucket__Token</c> and <c>GitBucket__GitPassword</c> are the .NET names of the config keys.
/// </summary>
/// <param name="Token">GitBucket__Token, else GITBUCKET_TOKEN; null when neither is set.</param>
public sealed record CredentialEnvironment(string? Token, string? GitPassword)
{
    public static CredentialEnvironment None { get; } = new(null, null);
}

/// <summary>
/// Where each secret comes from, at startup and after every save on the Setup page (docs/specs/dashboard-control.md
/// section 3): the token from the user secret, else the environment; the web password from the user secret, else
/// code_host.web_password in repos.yml (old setups); the git password from the user secret, else the environment,
/// else the token.
/// </summary>
public sealed class CredentialSources(
    UserSecretsStore store,
    CredentialEnvironment environment,
    string reposYmlWebPassword)
{
    public UserSecretsStore Store => store;

    /// <summary>A token stays when the user secret is removed: the environment brings one.</summary>
    public bool HasEnvironmentToken => !string.IsNullOrWhiteSpace(environment.Token);

    public CredentialValues Load()
    {
        var secrets = store.Read();

        return Resolve(secrets, environment, reposYmlWebPassword);
    }

    public static CredentialValues Resolve(
        IReadOnlyDictionary<string, string> secrets,
        CredentialEnvironment environment,
        string reposYmlWebPassword)
    {
        var secretToken = ValueOf(secrets, UserSecretsStore.TokenKey);
        var environmentToken = NullIfEmpty(environment.Token);
        var (token, tokenSource) = (secretToken, environmentToken) switch
        {
            ({ } fromSecrets, _) => (fromSecrets, CredentialSource.UserSecrets),
            (null, { } fromEnvironment) => (fromEnvironment, CredentialSource.Environment),
            _ => ("", CredentialSource.None),
        };

        var gitPassword = ValueOf(secrets, UserSecretsStore.GitPasswordKey)
            ?? NullIfEmpty(environment.GitPassword)
            ?? token;

        var secretWebPassword = ValueOf(secrets, UserSecretsStore.WebPasswordKey);
        var fileWebPassword = NullIfEmpty(reposYmlWebPassword);
        var (webPassword, webPasswordSource) = (secretWebPassword, fileWebPassword) switch
        {
            ({ } fromSecrets, _) => (fromSecrets, CredentialSource.UserSecrets),
            (null, { } fromFile) => (fromFile, CredentialSource.ReposYml),
            _ => ("", CredentialSource.None),
        };

        return new CredentialValues(token, tokenSource, gitPassword, webPassword, webPasswordSource);
    }

    private static string? ValueOf(IReadOnlyDictionary<string, string> secrets, string key)
    {
        var value = secrets.GetValueOrDefault(key);

        return NullIfEmpty(value);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
