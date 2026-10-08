using System.Net.Http.Headers;
using AiRunner.Configuration;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;

// Modes:
//   (none)          poll every pollIntervalSeconds and run jobs until Ctrl+C
//   --once          poll once, run the queued jobs, exit
//   --dry-run       print what would be queued; run nothing (with --once: a single poll)
//   --setup-labels  create the runner's labels in every repo of repos.yml
//   --copilot-test  start copilot as a child process and show its output
var once         = args.Contains("--once");
var dryRun       = args.Contains("--dry-run");
var copilotTest  = args.Contains("--copilot-test");
var setupLabels  = args.Contains("--setup-labels");
var configPath   = GetArg(args, "--config") ?? FindConfig();

var config = RunnerConfig.Load(configPath);

// No args passed to the host on purpose: flags like "--once" are not config keys.
var builder = Host.CreateApplicationBuilder();
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

var token = builder.Configuration["GitBucket:Token"]
            ?? Environment.GetEnvironmentVariable("GITBUCKET_TOKEN")
            ?? (copilotTest ? "" : throw new InvalidOperationException(
                "No GitBucket token. Run: dotnet user-secrets set GitBucket:Token <token> --project src/AiRunner"));
var identity = new RunnerIdentity { GitSecret = builder.Configuration["GitBucket:GitPassword"] ?? token };

// Configuration and Infrastructure: one instance each, every side effect behind an interface.
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton<IGitBucketClient>(_ => new GitBucketClient(new HttpClient
{
    BaseAddress = new Uri(config.GitBucket.BaseUrl.TrimEnd('/') + "/api/v3/"),
    Timeout = TimeSpan.FromSeconds(30),
    DefaultRequestHeaders =
    {
        Authorization = new AuthenticationHeaderValue("token", token),
        Accept = { new MediaTypeWithQualityHeaderValue("application/json") },
    },
}));
builder.Services.AddSingleton<IProcessRunner>(_ => new ProcessRunner());
builder.Services.AddSingleton<ICopilotRunner, CopilotRunner>();
builder.Services.AddSingleton<IGitClient, GitClient>();
builder.Services.AddSingleton<IWorkspace, Workspace>();
builder.Services.AddSingleton<IStateStore, SqliteStateStore>();
builder.Services.AddSingleton<ICostLog, CostLog>();
builder.Services.AddSingleton<JobLog>();
builder.Services.AddSingleton<AgentInstaller>();
builder.Services.AddSingleton<AgentSession>();

// Jobs: stateless, one per workflow.
builder.Services.AddSingleton<IJob, AnalyzeIssueJob>();
builder.Services.AddSingleton<IJob, BreakdownIssueJob>();
builder.Services.AddSingleton<IJob, ImplementIssueJob>();
builder.Services.AddSingleton<IJob, ReviewPullRequestJob>();
builder.Services.AddSingleton<IJob, SequenceSubIssuesJob>();
builder.Services.AddSingleton<IJob, FixReviewJob>();
builder.Services.AddSingleton<JobFactory>();

// Hosting.
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<Poller>();
builder.Services.AddSingleton<JobWorker>();
builder.Services.AddSingleton<LabelSetup>();
builder.Services.AddSingleton<CopilotSelfTest>();
var loop = !once && !copilotTest && !setupLabels;
if (loop)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Poller>());
    if (!dryRun) builder.Services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());
}

using var host = builder.Build();
var services = host.Services;
var ct = CancellationToken.None;

if (copilotTest)
{
    await services.GetRequiredService<CopilotSelfTest>().RunAsync(ct);
    return;
}

// Who are we on GitBucket? Needed for the scope guard and for git over HTTP.
identity.Login = (await services.GetRequiredService<IGitBucketClient>().GetCurrentUserAsync(ct)).Login;
var log = services.GetRequiredService<ILogger<Program>>();
log.LogInformation("ai-runner as {Login} on {Url}, {Repos} repo(s), config {Config}",
    identity.Login, config.GitBucket.BaseUrl, config.Repos.Count, Path.GetFullPath(configPath));

if (setupLabels)
{
    await services.GetRequiredService<LabelSetup>().RunAsync(ct);
    return;
}

services.GetRequiredService<Poller>().DryRun = dryRun;
if (!dryRun)
{
    services.GetRequiredService<AgentInstaller>().Install();
    services.GetRequiredService<JobLog>().Cleanup();
}

if (once)
{
    await services.GetRequiredService<Poller>().PollOnceAsync(ct);
    if (!dryRun) await services.GetRequiredService<JobWorker>().DrainAsync(ct);
    return;
}
await host.RunAsync();

static string? GetArg(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

// repos.yml in the current folder, or in the repo root when started with "dotnet run --project src/AiRunner".
static string FindConfig()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "repos.yml");
        if (File.Exists(candidate)) return candidate;
    }
    return "repos.yml";
}
