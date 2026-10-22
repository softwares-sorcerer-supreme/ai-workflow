using System.Net;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Hosting;

/// <summary>
/// The Setup page's changes inside the runner: a secret is checked against the code host, saved in user secrets only
/// when accepted, and swapped in live.
/// </summary>
public sealed class RunnerSetupTests : IDisposable
{
    private const string NewToken = "new-token-123";

    private SetupFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private void Use(SetupFixture fixture)
    {
        _fixture.Dispose();
        _fixture = fixture;
    }

    private static bool IsWakePending(RunnerCommands commands) => commands.WaitForWakeAsync(default).IsCompleted;

    // ---- token ----

    [Fact]
    public async Task An_accepted_token_is_saved_swapped_in_and_wakes_the_poller()
    {
        var identity = new RunnerIdentity();
        var commands = new RunnerCommands();
        var setup = _fixture.CreateSetup(identity, commands);

        var result = await setup.SaveTokenAsync($"  {NewToken}  ", default);

        Assert.Equal(SetupOutcome.Saved, result.Outcome);
        Assert.Contains("alice", result.Message);
        Assert.Equal([NewToken], _fixture.CheckedTokens);
        Assert.Equal(NewToken, _fixture.Store.Read().GetValueOrDefault(UserSecretsStore.TokenKey));
        Assert.Equal(NewToken, _fixture.Credentials.Current.Token);
        Assert.Equal(CredentialSource.UserSecrets, _fixture.Credentials.Current.TokenSource);
        Assert.Equal("alice", identity.Login);
        Assert.True(IsWakePending(commands));
        Assert.Equal(CredentialSource.UserSecrets, setup.Read().TokenSource);
    }

    [Fact]
    public async Task A_refused_token_is_not_saved()
    {
        _fixture.CheckHost.CurrentUserError = new CodeHostException(HttpStatusCode.Unauthorized, "401");
        var commands = new RunnerCommands();
        var setup = _fixture.CreateSetup(commands: commands);

        var result = await setup.SaveTokenAsync(NewToken, default);

        Assert.Equal(SetupOutcome.Refused, result.Outcome);
        Assert.Contains("The token was not accepted", result.Message);
        Assert.False(File.Exists(_fixture.Store.FilePath));
        Assert.False(_fixture.Credentials.IsTokenSet);
        Assert.False(IsWakePending(commands));
    }

    [Fact]
    public async Task An_unreachable_host_is_refused_and_names_the_base_url()
    {
        _fixture.CheckHost.CurrentUserError = new HttpRequestException("No such host is known.");
        var setup = _fixture.CreateSetup();

        var result = await setup.SaveTokenAsync(NewToken, default);

        Assert.Equal(SetupOutcome.Refused, result.Outcome);
        Assert.Contains($"GitBucket at {SetupFixture.BaseUrl} is not reachable (VPN?)", result.Message);
        Assert.Contains("code_host.base_url in repos.yml", result.Message);
        Assert.False(File.Exists(_fixture.Store.FilePath));
    }

    [Fact]
    public async Task An_empty_token_is_refused_without_a_check()
    {
        var setup = _fixture.CreateSetup();

        var result = await setup.SaveTokenAsync("   ", default);

        Assert.Equal(SetupOutcome.Refused, result.Outcome);
        Assert.Empty(_fixture.CheckedTokens);
    }

    [Fact]
    public async Task A_token_from_the_environment_cannot_be_removed_here()
    {
        Use(new SetupFixture(new CredentialEnvironment("env-token", null)));
        var setup = _fixture.CreateSetup();

        var result = await setup.RemoveTokenAsync(default);

        Assert.Equal(SetupOutcome.Conflict, result.Outcome);
        Assert.Contains("environment", result.Message);
        Assert.Equal("env-token", _fixture.Credentials.Current.Token);
        Assert.False(setup.Read().CanRemoveToken);
    }

    [Fact]
    public async Task A_saved_token_wins_over_the_environment_and_removing_it_falls_back_to_it()
    {
        Use(new SetupFixture(new CredentialEnvironment("env-token", null)));
        var identity = new RunnerIdentity { Login = "env-user" };
        var setup = _fixture.CreateSetup(identity);

        await setup.SaveTokenAsync(NewToken, default);
        var saved = setup.Read();
        _fixture.CheckHost.Login = "env-user";
        var removed = await setup.RemoveTokenAsync(default);

        Assert.Equal((CredentialSource.UserSecrets, true), (saved.TokenSource, saved.CanRemoveToken));
        Assert.Equal(SetupOutcome.Saved, removed.Outcome);
        Assert.Equal("env-token", _fixture.Credentials.Current.Token);
        Assert.Equal(CredentialSource.Environment, setup.Read().TokenSource);
        Assert.Equal([NewToken, "env-token"], _fixture.CheckedTokens);
        Assert.Equal("env-user", identity.Login);
    }

    [Fact]
    public async Task Removing_the_last_token_goes_back_to_setup_mode()
    {
        Use(new SetupFixture(seed: store => store.Set(UserSecretsStore.TokenKey, "old")));
        var identity = new RunnerIdentity { Login = "alice" };
        var setup = _fixture.CreateSetup(identity);

        var result = await setup.RemoveTokenAsync(default);

        Assert.Equal(SetupOutcome.Saved, result.Outcome);
        Assert.False(_fixture.Credentials.IsTokenSet);
        Assert.Null(_fixture.Store.Read().GetValueOrDefault(UserSecretsStore.TokenKey));
        Assert.Equal("", identity.Login);
        Assert.False(setup.Read().IsTokenSet);
    }

    [Fact]
    public async Task The_last_token_cannot_be_removed_while_a_job_runs()
    {
        Use(new SetupFixture(seed: store => store.Set(UserSecretsStore.TokenKey, "old")));
        var commands = new RunnerCommands();
        using var jobSource = new CancellationTokenSource();
        commands.RunStarted(12, jobSource);
        var setup = _fixture.CreateSetup(commands: commands);

        var whileRunning = await setup.RemoveTokenAsync(default);
        commands.RunEnded(12);
        var afterwards = await setup.RemoveTokenAsync(default);

        Assert.Equal(SetupOutcome.Conflict, whileRunning.Outcome);
        Assert.Contains("A job is running", whileRunning.Message);
        Assert.Equal(SetupOutcome.Saved, afterwards.Outcome);
        Assert.False(_fixture.Credentials.IsTokenSet);
    }

    [Fact]
    public async Task A_token_with_an_environment_token_behind_it_can_be_removed_while_a_job_runs()
    {
        Use(new SetupFixture(
            new CredentialEnvironment("env-token", null),
            seed: store => store.Set(UserSecretsStore.TokenKey, "old")));
        var commands = new RunnerCommands();
        using var jobSource = new CancellationTokenSource();
        commands.RunStarted(12, jobSource);
        var setup = _fixture.CreateSetup(commands: commands);

        var result = await setup.RemoveTokenAsync(default);

        Assert.Equal(SetupOutcome.Saved, result.Outcome);
        Assert.Equal("env-token", _fixture.Credentials.Current.Token);
    }

    [Fact]
    public async Task Saving_the_token_keeps_the_other_keys_of_the_secrets_file()
    {
        Use(new SetupFixture(seed: store =>
        {
            store.Set("Other:Key", "keep");
            store.Set(UserSecretsStore.GitPasswordKey, "git-pass");
        }));
        var setup = _fixture.CreateSetup();

        await setup.SaveTokenAsync(NewToken, default);

        Assert.Equal("keep", _fixture.Store.Read().GetValueOrDefault("Other:Key"));
        Assert.Equal("git-pass", _fixture.Credentials.Current.GitSecret);
    }

    [Fact]
    public async Task A_token_of_another_user_says_the_separate_git_password_is_still_used()
    {
        Use(new SetupFixture(seed: store => store.Set(UserSecretsStore.GitPasswordKey, "git-pass")));
        var identity = new RunnerIdentity { Login = "bob" };
        var setup = _fixture.CreateSetup(identity);

        var result = await setup.SaveTokenAsync(NewToken, default);

        Assert.Equal(SetupOutcome.Saved, result.Outcome);
        Assert.Contains(UserSecretsStore.GitPasswordKey, result.Message);
        Assert.Contains("bob", result.Message);
        Assert.DoesNotContain("git-pass", result.Message);
    }

    [Fact]
    public async Task A_token_of_the_same_user_says_nothing_about_the_git_password()
    {
        Use(new SetupFixture(seed: store => store.Set(UserSecretsStore.GitPasswordKey, "git-pass")));
        var identity = new RunnerIdentity { Login = "alice" };
        var setup = _fixture.CreateSetup(identity);

        var result = await setup.SaveTokenAsync(NewToken, default);

        Assert.DoesNotContain(UserSecretsStore.GitPasswordKey, result.Message);
    }

    // ---- web password ----

    [Fact]
    public async Task The_web_password_needs_the_token_first()
    {
        var setup = _fixture.CreateSetup();

        var result = await setup.SaveWebPasswordAsync(SetupFixture.RightWebPassword, default);

        Assert.Equal(SetupOutcome.Refused, result.Outcome);
        Assert.Contains("token first", result.Message);
        Assert.Empty(_fixture.CheckedWebPasswords);
    }

    [Fact]
    public async Task An_accepted_web_password_is_saved_and_a_wrong_one_is_not()
    {
        Use(new SetupFixture(seed: store => store.Set(UserSecretsStore.TokenKey, "t")));
        var setup = _fixture.CreateSetup(new RunnerIdentity { Login = "alice" });

        var wrong = await setup.SaveWebPasswordAsync("wrong-pw", default);
        var wrongState = _fixture.Credentials.Current;
        var right = await setup.SaveWebPasswordAsync(SetupFixture.RightWebPassword, default);

        Assert.Equal(SetupOutcome.Refused, wrong.Outcome);
        Assert.Contains("did not accept the web sign-in of alice", wrong.Message);
        Assert.False(wrongState.IsWebPasswordSet);
        Assert.Equal(SetupOutcome.Saved, right.Outcome);
        Assert.Equal(
            SetupFixture.RightWebPassword,
            _fixture.Store.Read().GetValueOrDefault(UserSecretsStore.WebPasswordKey));
        Assert.Equal(SetupFixture.RightWebPassword, _fixture.Credentials.Current.WebPassword);
        Assert.Equal(CredentialSource.UserSecrets, setup.Read().WebPasswordSource);
    }

    [Fact]
    public async Task A_web_password_in_repos_yml_cannot_be_removed_here_and_comes_back_after_a_saved_one()
    {
        Use(new SetupFixture(
            reposYmlWebPassword: "yml-pw",
            seed: store => store.Set(UserSecretsStore.WebPasswordKey, "secret-pw")));
        var setup = _fixture.CreateSetup();

        var first = await setup.RemoveWebPasswordAsync(default);
        var second = await setup.RemoveWebPasswordAsync(default);

        Assert.Equal(SetupOutcome.Saved, first.Outcome);
        Assert.Contains("repos.yml is used now", first.Message);
        Assert.Equal("yml-pw", _fixture.Credentials.Current.WebPassword);
        Assert.Equal(SetupOutcome.Conflict, second.Outcome);
        Assert.Contains("code_host.web_password in repos.yml", second.Message);
    }
}
