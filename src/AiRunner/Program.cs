using AiRunner.Configuration;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;
using AiRunner.Jobs;
using AiRunner.Web;

// Modes:
//   (none)                     poll every pollIntervalSeconds and run jobs until Ctrl+C (and serve the dashboard);
//                              with no token yet: setup mode (only the dashboard, open /setup to save the token)
//   --once                     poll once, run the queued jobs, exit
//   --dry-run                  print what would be queued; run nothing (with --once: a single poll)
//   --setup-labels             create the runner's labels in every repo of every project in repos.yml
//   --copilot-test             start copilot as a child process and show its output
//   --attachment-test URL      download one _attached file and show what GitBucket answered (no Copilot)
//   --close-test owner/repo#n  close one issue through the web form, the way the jobs do (no Copilot; it closes it!)
//   --openapi FILE             write the dashboard API's OpenAPI document (no config, no token, no network)
var mode = RunMode.Parse(args);

if (mode.OpenApiPath is not null)
{
    await OpenApiExport.WriteAsync(mode.OpenApiPath, CancellationToken.None);
    return;
}

var configPath = mode.ConfigPath ?? FindConfig();
var configFile = RunnerConfig.LoadFile(configPath);
var config = configFile.Config;
var fullConfigPath = Path.GetFullPath(configPath);

// No args passed to the host on purpose: flags like "--once" are not config keys. No AddUserSecrets either: the
// secrets file is read (and written by the Setup page) through UserSecretsStore only.
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

// The secrets: user secrets first, then the environment (GitBucket__Token is the .NET name of the config key),
// the web password last from repos.yml. With none, the loop waits in setup mode; every other mode stops.
// Read from the process environment itself, not from builder.Configuration: in the Development environment the host
// adds user secrets on its own, and a user secret must never count as an environment value.
var environmentToken = Environment.GetEnvironmentVariable("GitBucket__Token")
    ?? Environment.GetEnvironmentVariable("GITBUCKET_TOKEN");
var environmentGitPassword = Environment.GetEnvironmentVariable("GitBucket__GitPassword");
var environment = new CredentialEnvironment(environmentToken, environmentGitPassword);
var secretsStore = UserSecretsStore.ForAssembly(typeof(Program).Assembly);
var credentialSources = new CredentialSources(secretsStore, environment, configFile.WebPassword);
var credentialValues = credentialSources.Load();
var credentials = new RunnerCredentials(credentialValues);
var isSetupMode = !credentials.IsTokenSet && mode.CanStartInSetupMode;
if (!credentials.IsTokenSet && !isSetupMode && mode.NeedsToken)
{
    throw new InvalidOperationException(RunMode.NoTokenMessage(config.Dashboard.Port));
}

var identity = new RunnerIdentity();

// Configuration and Infrastructure: one instance each, every side effect behind an interface.
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(credentials);
builder.Services.AddSingleton(credentialSources);

// The code host (code_host.kind): the API client, the git remote and the credential check of its provider.
CodeHostRegistration.Add(builder.Services, configFile);

builder.Services.AddSingleton<IProcessRunner>(_ => new ProcessRunner());
builder.Services.AddSingleton<ICopilotRunner, CopilotRunner>();
builder.Services.AddSingleton<IGitClient, GitClient>();
builder.Services.AddSingleton<IWorkspace, Workspace>();
builder.Services.AddSingleton<SqliteStateStore>();
builder.Services.AddSingleton<IStateStore>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
builder.Services.AddSingleton<IJobReader>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
builder.Services.AddSingleton<IJobStepLog>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
builder.Services.AddSingleton<ISnapshotStore>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
builder.Services.AddSingleton<ISnapshotReader>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
builder.Services.AddSingleton<ICostReader>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
builder.Services.AddSingleton<ICostLog, CostLog>();
builder.Services.AddSingleton<JobLog>();
builder.Services.AddSingleton<AgentInstaller>();
builder.Services.AddSingleton(CopilotHome.FromUserProfile());
builder.Services.AddSingleton<AgentFiles>();
builder.Services.AddSingleton<AgentSession>();
builder.Services.AddSingleton<AttachmentFetcher>();

// Jobs: stateless, one per workflow.
builder.Services.AddSingleton<IJob, AnalyzeIssueJob>();
builder.Services.AddSingleton<IJob, BreakdownIssueJob>();
builder.Services.AddSingleton<IJob, ImplementIssueJob>();
builder.Services.AddSingleton<IJob, ReviewPullRequestJob>();
builder.Services.AddSingleton<IJob, SequenceSubIssuesJob>();
builder.Services.AddSingleton<IJob, FixReviewJob>();
builder.Services.AddSingleton<IJob, FinishIssueJob>();
builder.Services.AddSingleton<JobFactory>();
builder.Services.AddSingleton<JobLoader>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ClaimService>();

// Hosting.
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<SnapshotRecorder>();
builder.Services.AddSingleton<Poller>();
builder.Services.AddSingleton<JobWorker>();
builder.Services.AddSingleton<LabelSetup>();
builder.Services.AddSingleton<CopilotSelfTest>();
builder.Services.AddSingleton<AttachmentSelfTest>();
builder.Services.AddSingleton<CloseSelfTest>();
builder.Services.AddSingleton<RunnerCommands>();
builder.Services.AddSingleton<IRunnerSetup, RunnerSetup>();
builder.Services.AddSingleton<IRunnerActions, RunnerActions>();
builder.Services.AddSingleton<WorkspaceLocks>();
builder.Services.AddSingleton<DirectorySizes>();
builder.Services.AddSingleton<WorkspaceInspector>();
builder.Services.AddSingleton<IRunnerWorkspaces, RunnerWorkspaces>();
builder.Services.AddSingleton<AgentSync>();
builder.Services.AddSingleton<IRunnerAgents, RunnerAgents>();
builder.Services.AddSingleton(serviceProvider => new RunnerStatus(
    config.EffectiveRunnerName(),
    fullConfigPath,
    serviceProvider.GetRequiredService<TimeProvider>()));

if (mode.IsPollingLoop)
{
    builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<Poller>());

    if (!mode.DryRun)
    {
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<JobWorker>());
    }
}

if (mode.ShouldStartDashboard(config.Dashboard, isSetupMode))
{
    builder.Services.AddHostedService<DashboardHost>();
}

using var host = builder.Build();
var services = host.Services;
var cancellationToken = CancellationToken.None;

if (mode.CopilotTest)
{
    await services.GetRequiredService<CopilotSelfTest>().RunAsync(cancellationToken);
    return;
}

if (mode.AttachmentTestUrl is { } attachmentTestUrl)
{
    await services.GetRequiredService<AttachmentSelfTest>().RunAsync(attachmentTestUrl, cancellationToken);
    return;
}

if (mode.CloseTestIssue is { } closeTestIssue)
{
    await services.GetRequiredService<CloseSelfTest>().RunAsync(closeTestIssue, cancellationToken);
    return;
}

var logger = services.GetRequiredService<ILogger<Program>>();
if (isSetupMode)
{
    // No code-host call: the poller and the worker wait until the Setup page saves a token (RunnerSetup sets the
    // login).
    var setupUrl = RunMode.SetupUrl(config.Dashboard.Port);
    logger.LogWarning("No GitBucket token yet: open {SetupUrl}", setupUrl);
}
else
{
    // Who are we on the code host? Needed for the scope guard and for git over HTTP.
    var currentUser = await services.GetRequiredService<ICodeHost>().GetCurrentUserAsync(cancellationToken);
    identity.Login = currentUser.Login;
}

logger.LogInformation(
    "ai-runner as {Login} on {Url}, {Projects} project(s), {Repos} repo(s), config {Config}",
    isSetupMode ? "(setup mode)" : identity.Login,
    config.CodeHost.BaseUrl,
    config.Projects.Count,
    config.AllRepos().Count(),
    fullConfigPath);

if (mode.SetupLabels)
{
    await services.GetRequiredService<LabelSetup>().RunAsync(cancellationToken);
    return;
}

services.GetRequiredService<Poller>().DryRun = mode.DryRun;

if (!mode.DryRun)
{
    var copilotHome = services.GetRequiredService<CopilotHome>();
    services.GetRequiredService<AgentInstaller>().Install(copilotHome.Path);
    services.GetRequiredService<JobLog>().Cleanup();
}

if (mode.RunOnce)
{
    await services.GetRequiredService<Poller>().PollOnceAsync(cancellationToken);

    if (!mode.DryRun)
    {
        await services.GetRequiredService<JobWorker>().DrainAsync(cancellationToken);
    }

    return;
}

await host.RunAsync();

// repos.yml in the current folder, or in the repo root when started with "dotnet run --project src/AiRunner".
static string FindConfig()
{
    var currentDirectory = Directory.GetCurrentDirectory();
    for (var directory = new DirectoryInfo(currentDirectory);
         directory is not null;
         directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, "repos.yml");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return "repos.yml";
}
