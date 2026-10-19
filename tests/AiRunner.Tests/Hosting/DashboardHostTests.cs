using System.Net;
using System.Net.Sockets;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRunner.Tests.Hosting;

/// <summary>
/// DashboardHost inside a real generic host, on a real loopback port: the dashboard must never stop the runner.
/// </summary>
public sealed class DashboardHostTests : IDisposable
{
    private readonly ListLoggerProvider _logs = new();
    private readonly FakeCopilot _copilot = new() { Version = "GitHub Copilot CLI 1.2.3" };
    private readonly RunnerStatus _status = new("laptop-1", "repos.yml", TimeProvider.System);
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "ai-runner-no-ui-" + Guid.NewGuid());

    public void Dispose() => _logs.Dispose();

    private IHost BuildHost(int port)
    {
        var config = new RunnerConfig { Dashboard = new DashboardConfig { Enabled = true, Port = port } };
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });

        builder.Logging.AddProvider(_logs);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(_status);
        builder.Services.AddSingleton<ICopilotRunner>(_copilot);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHostedService(serviceProvider => new DashboardHost(
            config,
            _status,
            _copilot,
            TimeProvider.System,
            serviceProvider.GetRequiredService<ILogger<DashboardHost>>())
        {
            WebRoot = _webRoot,
        });

        return builder.Build();
    }

    private static DashboardHost DashboardOf(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<DashboardHost>().Single();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task A_taken_port_logs_one_warning_and_the_host_keeps_running()
    {
        using var occupant = new TcpListener(IPAddress.Loopback, 0);
        occupant.Start();
        var port = ((IPEndPoint)occupant.LocalEndpoint).Port;
        using var host = BuildHost(port);

        await host.StartAsync();
        var dashboardTask = DashboardOf(host).ExecuteTask!;
        await dashboardTask.WaitAsync(TimeSpan.FromSeconds(30));

        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.True(dashboardTask.IsCompletedSuccessfully);

        var warning = Assert.Single(_logs.Lines, line => line.Level >= LogLevel.Warning);
        Assert.Contains(port.ToString(), warning.Message);
        Assert.Contains("dashboard.port", warning.Message);

        await host.StopAsync();
    }

    [Fact]
    public async Task A_free_port_serves_the_health_route_on_127_0_0_1_and_reads_the_copilot_version()
    {
        var port = FreePort();
        using var host = BuildHost(port);

        await host.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var response = await GetWhenListeningAsync(client, "api/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"runnerName\":\"laptop-1\"", body);

        // The version is read right after the web app started.
        for (var attempt = 0; attempt < 50 && _status.Snapshot().CopilotVersion == RunnerStatus.UnknownVersion; attempt++)
        {
            await Task.Delay(100);
        }

        Assert.Equal("GitHub Copilot CLI 1.2.3", _status.Snapshot().CopilotVersion);

        await host.StopAsync();
        Assert.True(DashboardOf(host).ExecuteTask!.IsCompleted);
    }

    /// <summary>The web app starts in the background; retry briefly until it answers.</summary>
    private static async Task<HttpResponseMessage> GetWhenListeningAsync(HttpClient client, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await client.GetAsync(path);
                if (response.IsSuccessStatusCode || attempt >= 50)
                {
                    return response;
                }
            }
            catch (HttpRequestException) when (attempt < 50)
            {
                // Not listening yet.
            }

            await Task.Delay(100);
        }
    }
}
