using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

/// <summary>
/// --close-test owner/repo#n: closes one issue the way the jobs do (web sign-in + the issue page's close form) and
/// says what GitBucket answered. It costs nothing (no Copilot) but it DOES close the issue; use a test issue.
/// </summary>
public sealed class CloseSelfTest(IGitBucketClient gitBucketClient, ILogger<CloseSelfTest> logger)
{
    public async Task RunAsync(string issueText, CancellationToken cancellationToken)
    {
        var issue = IssueLinks.ParseRef(issueText);
        if (issue is null)
        {
            logger.LogError("FAILED: expected owner/repo#n, got '{IssueText}'", issueText);
            return;
        }

        logger.LogInformation("Closing {Issue}", issue);

        try
        {
            await gitBucketClient.CloseIssueAsync(issue.Repo, issue.Number, cancellationToken);
            logger.LogInformation("OK: {Issue} is closed", issue);
        }
        catch (Exception exception) when (exception is HttpRequestException or GitBucketException)
        {
            logger.LogError("FAILED: {Reason}", exception.Message);
        }
    }
}
