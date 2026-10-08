using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

/// <summary>--setup-labels: creates the labels from design doc section 5 in every repo of repos.yml.</summary>
public sealed class LabelSetup(RunnerConfig config, IGitBucketClient gitBucket, ILogger<LabelSetup> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var repo in config.Repos)
        {
            foreach (var (name, color, description) in Labels.All(config.ScopeLabel))
            {
                try
                {
                    var created = await gitBucket.CreateLabelAsync(repo.FullName, name, color, description, ct);
                    log.LogInformation("{Repo}: {Label} {Result}", repo, name, created ? "created" : "already exists");
                }
                catch (GitBucketException ex)
                {
                    log.LogError("{Repo}: {Label} failed: {Message}", repo, name, ex.Message);
                }
            }
        }
    }
}
