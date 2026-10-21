using System.Net;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;
using Microsoft.Extensions.Logging;

namespace AiRunner.Tests.Fakes;

/// <summary>
/// A <see cref="RunnerSetup"/> over a temp user-secrets file (never the real one), with a <see cref="FakeCodeHost"/>
/// behind the token check and a web sign-in that accepts only <see cref="RightWebPassword"/>. Every log line is kept.
/// </summary>
public sealed class SetupFixture : IDisposable
{
    public const string BaseUrl = "http://gb.test";
    public const string RightWebPassword = "right-web-pw";

    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-setup-").FullName;

    /// <param name="seed">Writes the secrets file before the runner reads it.</param>
    public SetupFixture(
        CredentialEnvironment? environment = null,
        string reposYmlWebPassword = "",
        Action<UserSecretsStore>? seed = null)
    {
        var secretsFile = Path.Combine(_folder, "secrets.json");
        Store = new UserSecretsStore(secretsFile);
        seed?.Invoke(Store);
        Sources = new CredentialSources(Store, environment ?? CredentialEnvironment.None, reposYmlWebPassword);
        var values = Sources.Load();
        Credentials = new RunnerCredentials(values);
    }

    public UserSecretsStore Store { get; }

    public CredentialSources Sources { get; }

    public RunnerCredentials Credentials { get; }

    /// <summary>The code host a token check talks to; set <c>CurrentUserError</c> to refuse the token.</summary>
    public FakeCodeHost CheckHost { get; } = new() { Login = "alice" };

    /// <summary>Every token the check was asked about, in order.</summary>
    public List<string> CheckedTokens { get; } = [];

    /// <summary>Every web password the check signed in with, in order.</summary>
    public List<string> CheckedWebPasswords { get; } = [];

    public ListLoggerProvider Logs { get; } = new();

    public RunnerConfig Config { get; } = new() { CodeHost = new CodeHostConfig { BaseUrl = BaseUrl } };

    public ICredentialCheck Check => new GitBucketCredentialCheck(
        token =>
        {
            CheckedTokens.Add(token);

            return CheckHost;
        },
        password =>
        {
            CheckedWebPasswords.Add(password);
            var location = password == RightWebPassword ? "/" : "/signin";
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri(location, UriKind.Relative) },
            });
            var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl + "/") };

            return new GitBucketWeb(client, RunnerCredentials.Of(webPassword: password));
        });

    /// <summary>A setup that shares the login and the poller wake with a runner (a kit's, or new ones).</summary>
    public RunnerSetup CreateSetup(RunnerIdentity? identity = null, RunnerCommands? commands = null)
    {
        var loggerFactory = new LoggerFactory([Logs]);

        return new RunnerSetup(
            Config,
            Credentials,
            Sources,
            Check,
            identity ?? new RunnerIdentity(),
            commands ?? new RunnerCommands(),
            loggerFactory.CreateLogger<RunnerSetup>());
    }

    public void Dispose()
    {
        Logs.Dispose();
        FileSystem.DeleteDirectory(_folder);
    }
}
