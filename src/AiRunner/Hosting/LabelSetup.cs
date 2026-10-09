using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

/// <summary>--setup-labels: creates the labels from design doc section 5 in every repo of repos.yml.</summary>
public sealed class LabelSetup(RunnerConfig config, IGitBucketClient gitBucketClient, ILogger<LabelSetup> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var repo in config.Repos)
        {
            foreach (var (name, color, description) in Labels.All(config.ScopeLabel))
            {
                try
                {
                    var newLabel = new NewLabel(name, color, description);
                    var created = await gitBucketClient.CreateLabelAsync(repo.FullName, newLabel, cancellationToken);

                    logger.LogInformation("{Repo}: {Label} {Result}", repo, name, created ? "created" : "already exists");
                }
                catch (GitBucketException exception)
                {
                    logger.LogError("{Repo}: {Label} failed: {Message}", repo, name, exception.Message);
                }
            }
        }
    }
}
