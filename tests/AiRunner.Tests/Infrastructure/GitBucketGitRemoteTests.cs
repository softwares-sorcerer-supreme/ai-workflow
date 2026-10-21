using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Tests.Infrastructure;

/// <summary>git reaches GitBucket the way it always did: same clone URL, auth header and PR head refspec.</summary>
public sealed class GitBucketGitRemoteTests
{
    private static GitBucketGitRemote Remote()
    {
        var config = RunnerConfig.Parse("""
            code_host: { kind: gitbucket, base_url: "http://gb.test:3000/" }
            """);
        var identity = new RunnerIdentity { Login = "bot" };
        var credentials = RunnerCredentials.Of(token: "s3cret");

        return new GitBucketGitRemote(config, identity, credentials);
    }

    [Fact]
    public void Repos_are_cloned_from_the_git_folder_of_the_host() =>
        Assert.Equal("http://gb.test:3000/git/acme/shop.git", Remote().UrlOf("acme/shop"));

    [Fact]
    public void Git_signs_in_with_basic_auth_of_the_login_and_the_git_secret() =>
        Assert.Equal("Authorization: Basic Ym90OnMzY3JldA==", Remote().AuthorizationHeader());

    [Fact]
    public void A_pull_head_is_fetched_from_refs_pull() =>
        Assert.Equal("+refs/pull/9/head:refs/remotes/pr/9", Remote().PullHeadRefspec(9));
}
