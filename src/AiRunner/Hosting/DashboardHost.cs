using AiRunner.Configuration;
using AiRunner.Infrastructure;
using AiRunner.Web;

namespace AiRunner.Hosting;

/// <summary>
/// Serves the local dashboard from inside the runner (docs/adr/0001). Registered only in the polling loop with
/// <c>dashboard.enabled: true</c>. The dashboard can never stop the real work: when the port is taken it logs one
/// warning and steps aside while the poller and worker keep running.
/// </summary>
public sealed class DashboardHost(
    RunnerConfig config,
    RunnerStatus status,
    ICopilotRunner copilot,
    TimeProvider time,
    IJobReader jobs,
    ILogger<DashboardHost> logger) : BackgroundService
{
    /// <summary>Where build-dashboard.ps1 puts the built UI (copied next to the runner's binaries).</summary>
    public string WebRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "wwwroot");

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var port = config.Dashboard.Port;
        var options = new DashboardAppOptions(config, status, time, WebRoot) { Jobs = jobs };
        var builder = DashboardApp.CreateBuilder(options);
        await using var app = DashboardApp.Build(builder, options);

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Any start failure is only a warning (the dashboard must never stop the runner); the usual cause is a
            // taken port, so the hint names the key, and the exception type tells the other causes apart.
            logger.LogWarning(
                "Dashboard not started on 127.0.0.1:{Port} ({ErrorType}: {Message}). " +
                "If the port is taken, free it or set another one in repos.yml (dashboard.port). " +
                "The runner keeps working.",
                port,
                exception.GetType().Name,
                exception.Message);
            return;
        }

        logger.LogInformation("Dashboard on http://localhost:{Port}/", port);
        await ReadCopilotVersionAsync(cancellationToken);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The runner is stopping: shut the web app down below.
        }

        await app.StopAsync(CancellationToken.None);
    }

    private async Task ReadCopilotVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await copilot.GetVersionAsync(cancellationToken);
            status.SetCopilotVersion(version);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Only shown on the dashboard: "unknown" is enough, the jobs report a missing CLI themselves.
            logger.LogDebug("copilot --version failed: {Message}", exception.Message);
        }
    }
}
