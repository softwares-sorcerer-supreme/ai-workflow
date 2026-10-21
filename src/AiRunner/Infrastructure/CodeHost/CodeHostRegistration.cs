using System.Net.Http.Headers;
using AiRunner.Configuration;

namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// Registers the provider that code_host.kind names: <see cref="ICodeHost"/>, <see cref="IGitRemote"/> and
/// <see cref="ICredentialCheck"/>. The one place that turns a kind into classes, so Program.cs knows no host's API.
/// The secrets come from the <see cref="RunnerCredentials"/> in the container, read per request.
/// </summary>
public static class CodeHostRegistration
{
    public static void Add(IServiceCollection services, ConfigFile configFile)
    {
        var kind = configFile.Config.CodeHost.Kind;
        switch (kind)
        {
            case CodeHostKinds.GitBucket:
                AddGitBucket(services, configFile);
                break;
            default:
                // ConfigResolver refuses an unknown kind; this is for a kind added there but not here.
                throw new InvalidOperationException($"code_host.kind '{kind}' has no provider.");
        }
    }

    /// <summary>
    /// Attachments are web routes, not /api/v3: a second client that never follows redirects (a redirect to the
    /// login page is reported, and the token cannot be sent on to another host). Closing an issue is a web form too
    /// (the API has no route for it): a third client with a session cookie, signed in with the web password.
    /// No client carries the token: each request gets it, so a check can use the same clients with its own token.
    /// </summary>
    private static void AddGitBucket(IServiceCollection services, ConfigFile configFile)
    {
        var baseUrl = configFile.Config.CodeHost.BaseUrl;
        var apiClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/v3/"),
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders = { Accept = { new MediaTypeWithQualityHeaderValue("application/json") } },
        };
        var downloadClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };

        GitBucketCodeHost CodeHostWith(RunnerCredentials credentials)
        {
            var web = GitBucketWeb.Create(baseUrl, credentials);

            return new GitBucketCodeHost(apiClient, downloadClient, web, credentials, baseUrl);
        }

        services.AddSingleton<ICodeHost>(serviceProvider =>
            CodeHostWith(serviceProvider.GetRequiredService<RunnerCredentials>()));
        services.AddSingleton<IGitRemote, GitBucketGitRemote>();
        services.AddSingleton<ICredentialCheck>(_ => new GitBucketCredentialCheck(
            token => CodeHostWith(RunnerCredentials.Of(token: token)),
            password => GitBucketWeb.Create(baseUrl, RunnerCredentials.Of(webPassword: password))));
    }
}
