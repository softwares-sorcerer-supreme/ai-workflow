using System.Net.Http.Headers;
using AiRunner.Configuration;

namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// Registers the provider that code_host.kind names: <see cref="ICodeHost"/> and <see cref="IGitRemote"/>. The one
/// place that turns a kind into classes, so Program.cs knows no host's API.
/// </summary>
public static class CodeHostRegistration
{
    public static void Add(IServiceCollection services, ConfigFile configFile, string token)
    {
        var kind = configFile.Config.CodeHost.Kind;
        if (kind != CodeHostKinds.GitBucket)
        {
            // ConfigResolver refuses an unknown kind; this is for a kind added there but not here.
            throw new InvalidOperationException($"code_host.kind '{kind}' has no provider.");
        }

        AddGitBucket(services, configFile, token);
    }

    /// <summary>
    /// Attachments are web routes, not /api/v3: a second client that never follows redirects (a redirect to the
    /// login page is reported, and the token cannot be sent on to another host). Closing an issue is a web form too
    /// (the API has no route for it): a third client with a session cookie, signed in with the web password.
    /// </summary>
    private static void AddGitBucket(IServiceCollection services, ConfigFile configFile, string token)
    {
        var baseUrl = configFile.Config.CodeHost.BaseUrl;

        services.AddSingleton<ICodeHost>(_ => new GitBucketCodeHost(
            new HttpClient
            {
                BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/v3/"),
                Timeout = TimeSpan.FromSeconds(30),
                DefaultRequestHeaders =
                {
                    Authorization = new AuthenticationHeaderValue("token", token),
                    Accept = { new MediaTypeWithQualityHeaderValue("application/json") },
                },
            },
            new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            {
                Timeout = TimeSpan.FromMinutes(2),
                DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("token", token) },
            },
            GitBucketWeb.Create(baseUrl, configFile.WebPassword),
            baseUrl));
        services.AddSingleton<IGitRemote, GitBucketGitRemote>();
    }
}
