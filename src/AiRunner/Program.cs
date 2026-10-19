using System.Net.Http.Headers;
using AiRunner.Configuration;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Web;

// Modes:
//   (none)          poll every pollIntervalSeconds and run jobs until Ctrl+C (and serve the dashboard when enabled)
//   --once          poll once, run the queued jobs, exit
//   --dry-run       print what would be queued; run nothing (with --once: a single poll)
//   --setup-labels  create the runner's labels in every repo of every project in repos.yml
//   --copilot-test  start copilot as a child process and show its output
//   --attachment-test URL  download one _attached file and show what GitBucket answered (no Copilot)
//   --openapi FILE  write the dashboard API's OpenAPI document (no config, no token, no network)
var mode = RunMode.Parse(args);

if (mode.OpenApiPath is not null)
{
    await OpenApiExport.WriteAsync(mode.OpenApiPath, CancellationToken.None);
    return;
}

var configPath = mode.ConfigPath ?? FindConfig();
var config = RunnerConfig.Load(configPath);
var fullConfigPath = Path.GetFullPath(configPath);

// No args passed to the host on purpose: flags like "--once" are not config keys.
var builder = Host.CreateApplicationBuilder();
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var token = builder.Configuration["GitBucket:Token"]
    ?? Environment.GetEnvironmentVariable("GITBUCKET_TOKEN")
    ?? (mode.CopilotTest ? "" : throw new InvalidOperationException(
        "No GitBucket token. Run: dotnet user-secrets set GitBucket:Token <token> --project src/AiRunner"));
var identity = new RunnerIdentity { GitSecret = builder.Configuration["GitBucket:GitPassword"] ?? token };

// Configuration and Infrastructure: one instance each, every side effect behind an interface.
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(identity);

// Attachments are web routes, not /api/v3: a second client that never follows redirects (a redirect to the
// login page is reported, and the token cannot be sent on to another host).
builder.Services.AddSingleton<IGitBucketClient>(_ => new GitBucketClient(
    new HttpClient
    {
        BaseAddress = new Uri(config.GitBucket.BaseUrl.TrimEnd('/') + "/api/v3/"),
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders =
        {
            Authorization = new AuthenticationHeaderValue("token", token),
            Accept = { new MediaTypeWithQualityHeaderValue("application/json") },
        },
    },
    new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromMinutes(2),
        DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("token", token) },
    }));
builder.Services.AddSingleton<IProcessRunner>(_ => new ProcessRunner());
builder.Services.AddSingleton<ICopilotRunner, CopilotRunner>();
builder.Services.AddSingleton<IGitClient, GitClient>();
builder.Services.AddSingleton<IWorkspace, Workspace>();
builder.Services.AddSingleton<SqliteStateStore>();
builder.Services.AddSingleton<IStateStore>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
builder.Services.AddSingleton<IJobReader>(serviceProvider => serviceProvider.GetRequiredService<SqliteStateStore>());
builder.Services.AddSingleton<ICostLog, CostLog>();
builder.Services.AddSingleton<JobLog>();
builder.Services.AddSingleton<AgentInstaller>();
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
builder.Services.AddSingleton<Poller>();
builder.Services.AddSingleton<JobWorker>();
builder.Services.AddSingleton<LabelSetup>();
builder.Services.AddSingleton<CopilotSelfTest>();
builder.Services.AddSingleton<AttachmentSelfTest>();
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

if (mode.ShouldStartDashboard(config.Dashboard))
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

// Who are we on GitBucket? Needed for the scope guard and for git over HTTP.
var currentUser = await services.GetRequiredService<IGitBucketClient>().GetCurrentUserAsync(cancellationToken);
identity.Login = currentUser.Login;

var logger = services.GetRequiredService<ILogger<Program>>();
logger.LogInformation(
    "ai-runner as {Login} on {Url}, {Projects} project(s), {Repos} repo(s), config {Config}",
    identity.Login,
    config.GitBucket.BaseUrl,
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
    services.GetRequiredService<AgentInstaller>().Install();
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
