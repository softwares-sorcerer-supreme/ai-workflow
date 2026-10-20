using System.Net;
using System.Text.Json.Serialization;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace AiRunner.Web;

/// <summary>What the dashboard web app is built from: the runner's own instances, never copies.</summary>
/// <param name="WebRoot">Folder of the built UI; it may be missing (then the "not built" page is served).</param>
public sealed record DashboardAppOptions(RunnerConfig Config, RunnerStatus Status, TimeProvider Time, string WebRoot)
{
    /// <summary>Read access to the job table; the default holds no jobs (the OpenAPI export needs none).</summary>
    public IJobReader Jobs { get; init; } = NoJobs.Instance;

    /// <summary>Read access to the snapshot; the default is empty (the OpenAPI export needs none).</summary>
    public ISnapshotReader Snapshot { get; init; } = NoSnapshot.Instance;

    /// <summary>Read access to the cost log; the default is empty (the OpenAPI export needs none).</summary>
    public ICostReader Cost { get; init; } = NoCost.Instance;
}

/// <summary>A snapshot reader for a runner that has no database yet.</summary>
public sealed class NoSnapshot : ISnapshotReader
{
    public static NoSnapshot Instance { get; } = new();

    public IReadOnlyList<IssueSnapshot> ListIssues() => [];

    public IReadOnlyList<PullSnapshot> ListPulls() => [];
}

/// <summary>A job reader for a runner that has no database yet.</summary>
public sealed class NoJobs : IJobReader
{
    public static NoJobs Instance { get; } = new();

    public IReadOnlyList<JobRecord> ListQueue() => [];

    public IReadOnlyList<JobRecord> ListActive() => [];

    public JobPage QueryHistory(JobQuery query) => new([], 0);

    public JobRecord? GetRecord(long id) => null;

    public IReadOnlyList<JobRecord> ListForItem(string repo, int number) => [];
}

/// <summary>
/// The dashboard's small web app. It is built from an empty builder on purpose: it reads no appsettings, no
/// environment variables and no user secrets, so the GitBucket token can never reach it, and it gets only the
/// services the API reads. Kestrel listens on 127.0.0.1; host filtering refuses any other Host header, so a web page
/// on another site cannot reach the API through DNS rebinding. Read-only: every method other than GET is refused.
/// </summary>
public static class DashboardApp
{
    private const string IndexFile = "index.html";

    private static readonly string[] AllowedHosts = ["localhost", "127.0.0.1"];

    public static WebApplicationBuilder CreateBuilder(DashboardAppOptions options)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DashboardApp).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Config.Dashboard.Port));

        // Its own console log: the 3 s polls of the UI must not flood the runner's log.
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddRoutingCore();
        builder.Services.AddHostFiltering(hostFiltering =>
        {
            hostFiltering.AllowedHosts = AllowedHosts;
            hostFiltering.AllowEmptyHosts = false;
        });
        builder.Services.AddOpenApi();

        // Numbers are numbers: the web default also accepts them as strings, which makes every int "number | string"
        // in the generated client.
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

        builder.Services.AddSingleton(options.Config);
        builder.Services.AddSingleton(options.Status);
        builder.Services.AddSingleton(options.Time);
        builder.Services.AddSingleton(options.Jobs);
        builder.Services.AddSingleton(options.Snapshot);
        builder.Services.AddSingleton(options.Cost);
        builder.Services.AddSingleton<CostReader>();
        builder.Services.AddSingleton<LogViewer>();
        builder.Services.AddSingleton<OverviewReader>();
        builder.Services.AddSingleton<IssueBoardReader>();
        builder.Services.AddSingleton<IssueReader>();
        builder.Services.AddSingleton<IssueDetailReader>();

        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder, DashboardAppOptions options)
    {
        var app = builder.Build();
        var indexFile = Path.Combine(options.WebRoot, IndexFile);
        var isUiBuilt = File.Exists(indexFile);

        app.Use(RejectNonGetAsync);
        app.UseHostFiltering();

        // Static files run before routing: once a fallback endpoint is chosen, the static file middleware steps aside.
        PhysicalFileProvider? uiFiles = null;
        if (isUiBuilt)
        {
            var webRoot = Path.GetFullPath(options.WebRoot);
            uiFiles = new PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = uiFiles });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = uiFiles });
        }

        app.UseRouting();

        app.MapOpenApi();
        app.MapDashboardApi();
        app.MapFallback("/api/{**rest}", () => Results.NotFound());

        // Any other route belongs to the single-page app, which does its own routing in the browser.
        if (uiFiles is not null)
        {
            app.MapFallbackToFile(IndexFile, new StaticFileOptions { FileProvider = uiFiles });
        }
        else
        {
            app.MapFallback(() => Results.Content(NotBuiltPage, "text/html; charset=utf-8"));
        }

        return app;
    }

    /// <summary>v1 is read-only (docs/adr/0003): anything but GET is a 405, whatever the route.</summary>
    private static async Task RejectNonGetAsync(HttpContext context, RequestDelegate next)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = HttpMethods.Get;
            return;
        }

        await next(context);
    }

    private const string NotBuiltPage = """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>ai-runner dashboard</title></head>
        <body style="font-family: system-ui, sans-serif; margin: 2rem;">
          <h1>Dashboard not built</h1>
          <p>The runner is serving the API, but the UI has not been built yet.</p>
          <p>Run <code>./build-dashboard.ps1</code> in the repository root, then restart the runner.</p>
          <p>The API is at <a href="/api/health">/api/health</a>.</p>
        </body>
        </html>
        """;
}
