using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AiRunner.Tests.Hosting;

/// <summary>
/// The runner's real registrations: they build with scope validation, so no singleton can hold the scoped config (it
/// would keep the startup config forever), and one scope gives one config.
/// </summary>
public sealed class RunnerServicesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runner-services-").FullName;

    public void Dispose()
    {
        // The job scope opened the state database; its pooled connection keeps the file open.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private ServiceProvider BuildRunnerServices()
    {
        var config = new RunnerConfig
        {
            CodeHost = new CodeHostConfig { BaseUrl = "http://gb.test" },
            RootDir = _folder,
        };
        var secretsFile = Path.Combine(_folder, "secrets.json");
        var credentialSources = new CredentialSources(
            new UserSecretsStore(secretsFile),
            CredentialEnvironment.None,
            "");
        var input = new RunnerServicesInput(
            RunnerConfigSource.Fixed(config),
            RunnerCredentials.Of(token: "t"),
            credentialSources,
            new RunnerIdentity(),
            new CopilotHome(Path.Combine(_folder, "copilot-home")));
        var services = new ServiceCollection();
        services.AddLogging();
        RunnerServices.Add(services, input);

        return services.BuildServiceProvider(RunnerServices.ProviderOptions);
    }

    [Fact]
    public void The_registrations_build_with_scope_validation()
    {
        using var provider = BuildRunnerServices();

        Assert.NotNull(provider.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public void The_config_cannot_be_taken_outside_a_scope()
    {
        using var provider = BuildRunnerServices();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<RunnerConfig>());
    }

    [Fact]
    public void A_scope_gives_its_jobs_the_config_it_resolved_first()
    {
        using var provider = BuildRunnerServices();
        using var scope = provider.CreateScope();

        var config = scope.ServiceProvider.GetRequiredService<RunnerConfig>();
        var again = scope.ServiceProvider.GetRequiredService<RunnerConfig>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobFactory>();

        Assert.Same(config, again);
        Assert.NotNull(jobs.Get(JobType.ImplementIssue));
    }
}
