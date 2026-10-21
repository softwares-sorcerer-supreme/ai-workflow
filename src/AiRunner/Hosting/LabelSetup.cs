using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Hosting;

/// <summary>--setup-labels: creates the labels from design doc section 5 in every repo of every project, issues-only repos included.</summary>
public sealed class LabelSetup(RunnerConfig config, ICodeHost codeHost, ILogger<LabelSetup> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var project in config.Projects)
        {
            var settings = project.Settings;
            var labels = Labels.All(settings.TriggerLabel, settings.TriggerKeyword);

            foreach (var repo in project.Repos)
            {
                foreach (var (name, color, description) in labels)
                {
                    try
                    {
                        var newLabel = new NewLabel(name, color, description);
                        var created = await codeHost.CreateLabelAsync(repo, newLabel, cancellationToken);

                        logger.LogInformation("{Repo}: {Label} {Result}", repo, name, created ? "created" : "already exists");
                    }
                    catch (CodeHostException exception)
                    {
                        logger.LogError("{Repo}: {Label} failed: {Message}", repo, name, exception.Message);
                    }
                }
            }
        }
    }
}
