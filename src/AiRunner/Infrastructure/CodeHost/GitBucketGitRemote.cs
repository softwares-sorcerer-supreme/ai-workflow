using System.Text;
using AiRunner.Configuration;

namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// git over HTTP on GitBucket: repos under /git/, basic auth with the login and the git secret (the token, or
/// GitBucket:GitPassword), PR heads under refs/pull/N/head (not on every GitBucket version). The secret is read per
/// git call, so a token saved on the Setup page is used from the next call on.
/// </summary>
public sealed class GitBucketGitRemote(
    RunnerConfigSource configSource,
    RunnerIdentity identity,
    RunnerCredentials credentials)
    : IGitRemote
{
    /// <summary>Reads only keys that need a restart (and the root folder), so the live config is always right.</summary>
    private RunnerConfig Config => configSource.Current;

    public string UrlOf(string repo) => $"{Config.CodeHost.BaseUrl.TrimEnd('/')}/git/{repo}.git";

    public string AuthorizationHeader()
    {
        var gitSecret = credentials.Current.GitSecret;
        var credentialBytes = Encoding.UTF8.GetBytes($"{identity.Login}:{gitSecret}");
        var basicCredentials = Convert.ToBase64String(credentialBytes);

        return $"Authorization: Basic {basicCredentials}";
    }

    public string PullHeadRefspec(int number) => $"+refs/pull/{number}/head:refs/remotes/pr/{number}";
}
