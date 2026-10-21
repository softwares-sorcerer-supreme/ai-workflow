using AiRunner.Configuration;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;
using AiRunner.Jobs;

namespace AiRunner.Hosting;

/// <summary>What the runner's services are built from: the instances Program.cs makes before the container.</summary>
public sealed record RunnerServicesInput(
    RunnerConfigSource ConfigSource,
    RunnerCredentials Credentials,
    CredentialSources CredentialSources,
    RunnerIdentity Identity,
    CopilotHome CopilotHome);

/// <summary>
/// The runner's DI registrations (Program.cs adds the hosted services of its mode). The config is live
/// (<see cref="RunnerConfigSource"/>), so lifetimes carry a rule: <see cref="RunnerConfig"/> is scoped, and the first
/// resolution in a scope pins that scope's snapshot. A job runs in its own scope (<see cref="JobWorker"/>), a poll in
/// its own (<see cref="Poller"/>), so everything that takes a <see cref="RunnerConfig"/> is scoped too and keeps one
/// config to its end. Singletons take the source and read <see cref="RunnerConfigSource.Current"/> when they act.
/// Scope validation is on (<see cref="ProviderOptions"/>): a singleton that captured the scoped config would keep the
/// startup config forever, so the container refuses to build instead.
/// </summary>
public static class RunnerServices
{
    public static ServiceProviderOptions ProviderOptions { get; } = new()
    {
        ValidateScopes = true,
        ValidateOnBuild = true,
    };

    public static void Add(IServiceCollection services, RunnerServicesInput input)
    {
        var configSource = input.ConfigSource;

        // Configuration: the live source, and the snapshot of the current scope.
        services.AddSingleton(configSource);
        services.AddScoped(serviceProvider => serviceProvider.GetRequiredService<RunnerConfigSource>().Current);
        services.AddSingleton(input.Identity);
        services.AddSingleton(input.Credentials);
        services.AddSingleton(input.CredentialSources);

        // The code host (code_host.kind): the API client, the git remote and the credential check of its provider.
        // Kind and base URL need a restart, so the startup values are the right ones.
        CodeHostRegistration.Add(services, configSource.Current.CodeHost);

        // Infrastructure: every side effect behind an interface.
        services.AddSingleton<IProcessRunner>(_ => new ProcessRunner());
        services.AddScoped<ICopilotRunner, CopilotRunner>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IWorkspace, Workspace>();
        services.AddSingleton<SqliteStateStore>();
        services.AddSingleton<IStateStore>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
        services.AddSingleton<IJobReader>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
        services.AddSingleton<IJobStepLog>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
        services.AddSingleton<ISnapshotStore>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
        services.AddSingleton<ISnapshotReader>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
        services.AddSingleton<ICostReader>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
        services.AddSingleton<ICostLog, CostLog>();
        services.AddSingleton<JobLog>();
        services.AddSingleton<AgentInstaller>();
        services.AddSingleton(input.CopilotHome);
        services.AddSingleton<AgentFiles>();
        services.AddSingleton<ConfigFileStore>();

        // Jobs: one per workflow, scoped like everything that reads the job's config.
        services.AddScoped<AgentSession>();
        services.AddScoped<AttachmentFetcher>();
        services.AddScoped<IJob, AnalyzeIssueJob>();
        services.AddScoped<IJob, BreakdownIssueJob>();
        services.AddScoped<IJob, ImplementIssueJob>();
        services.AddScoped<IJob, ReviewPullRequestJob>();
        services.AddScoped<IJob, SequenceSubIssuesJob>();
        services.AddScoped<IJob, FixReviewJob>();
        services.AddScoped<IJob, FinishIssueJob>();
        services.AddScoped<JobFactory>();
        services.AddScoped<JobLoader>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ClaimService>();

        // Hosting.
        services.AddSingleton<JobQueue>();
        services.AddScoped<SnapshotRecorder>();
        services.AddSingleton<Poller>();
        services.AddSingleton<JobWorker>();
        services.AddSingleton<LabelSetup>();
        services.AddScoped<CopilotSelfTest>();
        services.AddSingleton<AttachmentSelfTest>();
        services.AddSingleton<CloseSelfTest>();
        services.AddSingleton<RunnerCommands>();
        services.AddSingleton<IRunnerSetup, RunnerSetup>();
        services.AddSingleton<IRunnerActions, RunnerActions>();
        services.AddSingleton<IRunnerSettings, RunnerSettings>();
        services.AddSingleton<WorkspaceLocks>();
        services.AddSingleton<DirectorySizes>();
        services.AddSingleton<WorkspaceInspector>();
        services.AddSingleton<IRunnerWorkspaces, RunnerWorkspaces>();
        services.AddSingleton<AgentSync>();
        services.AddSingleton<IRunnerAgents, RunnerAgents>();
        services.AddSingleton(serviceProvider => new RunnerStatus(
            configSource.Current.EffectiveRunnerName(),
            configSource.Path ?? "",
            serviceProvider.GetRequiredService<TimeProvider>()));
    }
}
