namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// How git reaches the code host: hosts differ in the clone URL, the auth header and where a PR's head commit lives.
/// Kept apart from <see cref="ICodeHost"/> so <see cref="GitClient"/> needs no HTTP client.
/// </summary>
public interface IGitRemote
{
    /// <summary>The clone URL of "owner/name".</summary>
    string UrlOf(string repo);

    /// <summary>
    /// The value of git's http.extraHeader. It goes into an environment variable of each git call, never onto a
    /// command line or into .git/config.
    /// </summary>
    string AuthorizationHeader();

    /// <summary>Refspec that fetches a PR's head commit to refs/remotes/pr/N; a host may not have it.</summary>
    string PullHeadRefspec(int number);
}
