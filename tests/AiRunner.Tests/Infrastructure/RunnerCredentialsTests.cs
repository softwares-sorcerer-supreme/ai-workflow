using AiRunner.Infrastructure;

namespace AiRunner.Tests.Infrastructure;

/// <summary>Where each secret comes from, and how the live secrets are swapped.</summary>
public class RunnerCredentialsTests
{
    private static readonly Dictionary<string, string> NoSecrets = [];

    private static CredentialValues Resolve(
        Dictionary<string, string> secrets,
        CredentialEnvironment? environment = null,
        string reposYmlWebPassword = "") =>
        CredentialSources.Resolve(secrets, environment ?? CredentialEnvironment.None, reposYmlWebPassword);

    [Fact]
    public void Nothing_set_means_setup_mode()
    {
        var values = Resolve(NoSecrets);

        Assert.False(values.IsTokenSet);
        Assert.Equal(CredentialSource.None, values.TokenSource);
        Assert.Equal(CredentialSource.None, values.WebPasswordSource);
    }

    [Fact]
    public void The_environment_token_still_works()
    {
        var values = Resolve(NoSecrets, new CredentialEnvironment("env-token", null));

        Assert.Equal("env-token", values.Token);
        Assert.Equal(CredentialSource.Environment, values.TokenSource);
        Assert.Equal("env-token", values.GitSecret);
    }

    [Fact]
    public void A_saved_user_secret_wins_over_the_environment()
    {
        var secrets = new Dictionary<string, string> { [UserSecretsStore.TokenKey] = "secret-token" };

        var values = Resolve(secrets, new CredentialEnvironment("env-token", null));

        Assert.Equal("secret-token", values.Token);
        Assert.Equal(CredentialSource.UserSecrets, values.TokenSource);
    }

    [Fact]
    public void The_git_password_is_the_user_secret_then_the_environment_then_the_token()
    {
        var withSecret = new Dictionary<string, string>
        {
            [UserSecretsStore.TokenKey] = "t",
            [UserSecretsStore.GitPasswordKey] = "git-secret",
        };
        var withToken = new Dictionary<string, string> { [UserSecretsStore.TokenKey] = "t" };
        var environment = new CredentialEnvironment(null, "git-env");

        Assert.Equal("git-secret", Resolve(withSecret, environment).GitSecret);
        Assert.Equal("git-env", Resolve(withToken, environment).GitSecret);
        Assert.Equal("t", Resolve(withToken).GitSecret);
    }

    [Fact]
    public void The_web_password_in_repos_yml_still_works_and_a_user_secret_wins_over_it()
    {
        var secrets = new Dictionary<string, string> { [UserSecretsStore.WebPasswordKey] = "web-secret" };

        var fromFile = Resolve(NoSecrets, reposYmlWebPassword: "web-yml");
        var fromSecrets = Resolve(secrets, reposYmlWebPassword: "web-yml");

        Assert.Equal("web-yml", fromFile.WebPassword);
        Assert.Equal(CredentialSource.ReposYml, fromFile.WebPasswordSource);
        Assert.Equal("web-secret", fromSecrets.WebPassword);
        Assert.Equal(CredentialSource.UserSecrets, fromSecrets.WebPasswordSource);
    }

    [Fact]
    public void Blank_values_count_as_not_set()
    {
        var secrets = new Dictionary<string, string> { [UserSecretsStore.TokenKey] = "  " };

        var values = Resolve(secrets, new CredentialEnvironment("", null));

        Assert.False(values.IsTokenSet);
    }

    [Fact]
    public async Task Waiting_for_a_token_ends_when_one_is_applied()
    {
        var credentials = new RunnerCredentials(CredentialValues.Empty);
        var wait = credentials.WaitForTokenAsync(default);

        Assert.False(wait.IsCompleted);

        credentials.Apply(RunnerCredentials.Of(token: "t").Current);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, credentials.Version);
        Assert.True(credentials.WaitForTokenAsync(default).IsCompleted);
    }

    [Fact]
    public void Redact_hides_every_current_secret()
    {
        var credentials = RunnerCredentials.Of(token: "tok-123", webPassword: "pw-456");

        var redacted = credentials.Redact("echo tok-123 and pw-456");

        Assert.Equal("echo *** and ***", redacted);
    }
}
