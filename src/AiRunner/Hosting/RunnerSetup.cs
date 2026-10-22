using AiRunner.Configuration;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Hosting;

/// <summary>
/// What the Setup page shows: where each secret comes from and whether it is set, never a secret itself.
/// </summary>
/// <param name="SecretsFile">The user-secrets file the page saves to.</param>
public sealed record SetupState(
    bool IsTokenSet,
    string TokenSource,
    string Login,
    string BaseUrl,
    bool IsWebPasswordSet,
    string WebPasswordSource,
    string SecretsFile)
{
    /// <summary>Only a user secret can be removed here; the environment and repos.yml change where they live.</summary>
    public bool CanRemoveToken => TokenSource == CredentialSource.UserSecrets;

    public bool CanRemoveWebPassword => WebPasswordSource == CredentialSource.UserSecrets;
}

public enum SetupOutcome
{
    Saved,

    /// <summary>The value was checked and refused, or is missing: nothing was saved.</summary>
    Refused,

    /// <summary>The change cannot be made here (a value from the environment or repos.yml).</summary>
    Conflict,
}

/// <summary>The answer to one setup change, with a message the page shows as it is.</summary>
public sealed record SetupResult(SetupOutcome Outcome, string Message);

/// <summary>
/// The Setup page's way into the runner (docs/specs/dashboard-control.md section 3). The web app can give it a secret
/// to check and save, but can never read one back: nothing here returns a secret.
/// </summary>
public interface IRunnerSetup
{
    /// <summary>Setup mode: the runner has no token, so nothing polls and no job runs until one is saved.</summary>
    bool IsSetupNeeded { get; }

    SetupState Read();

    Task<SetupResult> SaveTokenAsync(string token, CancellationToken cancellationToken);

    Task<SetupResult> RemoveTokenAsync(CancellationToken cancellationToken);

    Task<SetupResult> SaveWebPasswordAsync(string password, CancellationToken cancellationToken);

    Task<SetupResult> RemoveWebPasswordAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Checks a new secret against the code host, saves it in user secrets and swaps it in without a restart. A refused
/// secret is never saved. An accepted token also sets the login (another token may belong to another user) and wakes
/// the poller, which ends setup mode. Shared by the runner and the dashboard web app, like
/// <see cref="RunnerCommands"/>. Secrets are never logged and never part of a message.
/// </summary>
public sealed class RunnerSetup(
    RunnerConfigSource configSource,
    RunnerCredentials credentials,
    CredentialSources sources,
    ICredentialCheck check,
    RunnerIdentity identity,
    RunnerCommands commands,
    ILogger<RunnerSetup> logger) : IRunnerSetup
{
    /// <summary>
    /// Reads only keys that need a restart (and the root folder), so the live config is always right.
    /// </summary>
    private RunnerConfig Config => configSource.Current;

    /// <summary>One change at a time: two pages saving at once must not interleave check, save and swap.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string BaseUrl => Config.CodeHost.BaseUrl;

    public bool IsSetupNeeded => !credentials.IsTokenSet;

    public SetupState Read()
    {
        var values = credentials.Current;

        return new SetupState(
            IsTokenSet: values.IsTokenSet,
            TokenSource: values.TokenSource,
            Login: identity.Login,
            BaseUrl: BaseUrl,
            IsWebPasswordSet: values.IsWebPasswordSet,
            WebPasswordSource: values.WebPasswordSource,
            SecretsFile: sources.Store.FilePath);
    }

    public async Task<SetupResult> SaveTokenAsync(string token, CancellationToken cancellationToken)
    {
        var newToken = token.Trim();
        if (newToken.Length == 0)
        {
            return new SetupResult(SetupOutcome.Refused, "Paste a token first.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var answer = await check.CheckTokenAsync(newToken, cancellationToken);
            if (!answer.IsAccepted)
            {
                logger.LogWarning("Setup: the new GitBucket token was refused ({Verdict}).", answer.Verdict);

                return Refusal(answer, "The token was not accepted by GitBucket. Nothing was saved.");
            }

            // The login first: swapping the token in wakes a poller that waits in setup mode, and its first poll
            // must already know who the runner is (its own PRs are found by the login).
            var previousLogin = identity.Login;
            identity.Login = answer.Login;
            var failure = TryChangeStore(store => store.Set(UserSecretsStore.TokenKey, newToken));
            if (failure is not null)
            {
                identity.Login = previousLogin;

                return failure;
            }

            ApplyStoredSecrets();

            commands.WakePoller();
            logger.LogInformation(
                "Setup: GitBucket token saved in user secrets; the runner works as {Login}.",
                answer.Login);

            var saved = $"Token accepted for {answer.Login} and saved in user secrets. Polling starts now.";

            return new SetupResult(SetupOutcome.Saved, saved + GitPasswordNote(previousLogin, answer.Login));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A git password set apart from the token belongs to a user too, and a token of another user does not replace it:
    /// git would keep signing in as the old one. Said once on the save, never changed silently.
    /// </summary>
    private string GitPasswordNote(string previousLogin, string newLogin)
    {
        var values = credentials.Current;
        var hasOwnGitPassword = values.GitSecret != values.Token;
        var isOtherUser = previousLogin.Length > 0 &&
            !string.Equals(previousLogin, newLogin, StringComparison.OrdinalIgnoreCase);

        if (!hasOwnGitPassword || !isOtherUser)
        {
            return "";
        }

        return $" Git still signs in with the separate git password ({UserSecretsStore.GitPasswordKey}); change it " +
            $"if it belongs to {previousLogin}.";
    }

    public async Task<SetupResult> RemoveTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var source = credentials.Current.TokenSource;
            if (source == CredentialSource.Environment)
            {
                return new SetupResult(
                    SetupOutcome.Conflict,
                    $"The token comes from the environment ({CredentialEnvironment.TokenVariables}), so it cannot be " +
                    "removed here. Remove the variable and restart the runner.");
            }

            if (source != CredentialSource.UserSecrets)
            {
                return new SetupResult(SetupOutcome.Conflict, "No token is saved.");
            }

            // Without a token the running job could not even report its end or release its claim.
            if (!sources.HasEnvironmentToken && commands.IsJobRunning)
            {
                return new SetupResult(
                    SetupOutcome.Conflict,
                    "A job is running and needs the token to finish. Pause the runner, wait for the job to end, " +
                    "then remove the token.");
            }

            var failure = TryChangeStore(store => store.Remove(UserSecretsStore.TokenKey));
            if (failure is not null)
            {
                return failure;
            }

            var message = await SwapInRemainingTokenAsync(cancellationToken);
            logger.LogInformation("Setup: GitBucket token removed from user secrets.");

            return new SetupResult(SetupOutcome.Saved, message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SetupResult> SaveWebPasswordAsync(string password, CancellationToken cancellationToken)
    {
        if (password.Length == 0)
        {
            return new SetupResult(SetupOutcome.Refused, "Type the web password first.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var login = identity.Login;
            if (!credentials.IsTokenSet || login.Length == 0)
            {
                return new SetupResult(
                    SetupOutcome.Refused,
                    "Save the token first: the web password is the sign-in password of the token's user.");
            }

            var answer = await check.CheckWebPasswordAsync(login, password, cancellationToken);
            if (!answer.IsAccepted)
            {
                logger.LogWarning(
                    "Setup: the new web password of {Login} was refused ({Verdict}).",
                    login,
                    answer.Verdict);

                return Refusal(answer, $"GitBucket did not accept the web sign-in of {login}. Nothing was saved.");
            }

            var failure = TryChangeStore(store => store.Set(UserSecretsStore.WebPasswordKey, password));
            if (failure is not null)
            {
                return failure;
            }

            ApplyStoredSecrets();

            logger.LogInformation("Setup: web password of {Login} saved in user secrets.", login);

            return new SetupResult(SetupOutcome.Saved, $"Web password accepted for {login} and saved in user secrets.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SetupResult> RemoveWebPasswordAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var source = credentials.Current.WebPasswordSource;
            if (source == CredentialSource.ReposYml)
            {
                return new SetupResult(
                    SetupOutcome.Conflict,
                    "The web password comes from code_host.web_password in repos.yml, so it cannot be removed here. " +
                    "Remove it there.");
            }

            if (source != CredentialSource.UserSecrets)
            {
                return new SetupResult(SetupOutcome.Conflict, "No web password is saved.");
            }

            var failure = TryChangeStore(store => store.Remove(UserSecretsStore.WebPasswordKey));
            if (failure is not null)
            {
                return failure;
            }

            ApplyStoredSecrets();

            logger.LogInformation("Setup: web password removed from user secrets.");
            var message = credentials.Current.IsWebPasswordSet
                ? "Web password removed from user secrets; code_host.web_password in repos.yml is used now."
                : "Web password removed. Closing issues after a merge needs one.";

            return new SetupResult(SetupOutcome.Saved, message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The environment's token, if any, takes over. Its user is looked up before the swap, because the swap wakes a
    /// poller that waits in setup mode and its first poll must already know the login.
    /// </summary>
    private async Task<string> SwapInRemainingTokenAsync(CancellationToken cancellationToken)
    {
        var values = sources.Load();
        if (!values.IsTokenSet)
        {
            identity.Login = "";
            credentials.Apply(values);

            return "Token removed. The runner waits in setup mode until a new one is saved.";
        }

        var answer = await check.CheckTokenAsync(values.Token, cancellationToken);
        identity.Login = answer.IsAccepted ? answer.Login : "";
        credentials.Apply(values);
        commands.WakePoller();

        return answer.IsAccepted
            ? $"Token removed from user secrets; the token from the environment is used now ({answer.Login})."
            : "Token removed from user secrets; the token from the environment is used now, but GitBucket did not " +
              "accept it.";
    }

    private SetupResult Refusal(CredentialCheck answer, string notAccepted) => answer.Verdict switch
    {
        CheckVerdict.Unreachable => new SetupResult(
            SetupOutcome.Refused,
            $"GitBucket at {BaseUrl} is not reachable (VPN?). Nothing was saved. " +
            "The address is code_host.base_url in repos.yml."),
        CheckVerdict.Failed when answer.Status is { } status => new SetupResult(
            SetupOutcome.Refused,
            $"GitBucket at {BaseUrl} answered {status}. Nothing was saved."),
        CheckVerdict.Failed => new SetupResult(
            SetupOutcome.Refused,
            $"The check against GitBucket at {BaseUrl} failed. Nothing was saved."),
        _ => new SetupResult(SetupOutcome.Refused, notAccepted),
    };

    /// <summary>Changes the secrets file; null when it went through, else the refusal.</summary>
    private SetupResult? TryChangeStore(Action<UserSecretsStore> change)
    {
        try
        {
            change(sources.Store);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            return StoreFailure(exception);
        }

        return null;
    }

    /// <summary>Reads every source again and swaps the result in.</summary>
    private void ApplyStoredSecrets()
    {
        var values = sources.Load();
        credentials.Apply(values);
    }

    private static bool IsStoreFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException;

    /// <summary>Names the file and the kind of failure only: an I/O message should not quote a secret.</summary>
    private SetupResult StoreFailure(Exception exception)
    {
        var file = sources.Store.FilePath;
        var errorType = exception.GetType().Name;
        logger.LogError("Setup: cannot change the user-secrets file {File} ({ErrorType}).", file, errorType);

        return new SetupResult(
            SetupOutcome.Refused,
            $"Cannot change the user-secrets file {file} ({errorType}). Nothing was saved.");
    }
}
