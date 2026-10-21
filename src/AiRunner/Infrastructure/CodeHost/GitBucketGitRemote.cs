using System.Text;
using AiRunner.Configuration;

namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// git over HTTP on GitBucket: repos under /git/, basic auth with the login and the git secret (the token, or
/// GitBucket:GitPassword), PR heads under refs/pull/N/head (not on every GitBucket version).
/// </summary>
public sealed class GitBucketGitRemote(RunnerConfig config, RunnerIdentity identity) : IGitRemote
{
    public string UrlOf(string repo) => $"{config.CodeHost.BaseUrl.TrimEnd('/')}/git/{repo}.git";

    public string AuthorizationHeader()
    {
        var credentialBytes = Encoding.UTF8.GetBytes($"{identity.Login}:{identity.GitSecret}");
        var basicCredentials = Convert.ToBase64String(credentialBytes);

        return $"Authorization: Basic {basicCredentials}";
    }

    public string PullHeadRefspec(int number) => $"+refs/pull/{number}/head:refs/remotes/pr/{number}";
}
