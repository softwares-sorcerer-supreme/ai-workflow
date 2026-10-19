using AiRunner.Configuration;
using AiRunner.Hosting;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AiRunner.Web;

/// <summary>
/// For <c>--openapi &lt;file&gt;</c>: writes the dashboard API's OpenAPI document, the input of the generated UI client.
/// It needs no repos.yml, no token and no GitBucket, so build-dashboard.ps1 can run it anywhere. The document is the
/// same one <c>/openapi/v1.json</c> serves, without the request-specific <c>servers</c> list.
/// </summary>
public static class OpenApiExport
{
    private const string DocumentName = "v1";

    public static async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        // Port 0: any free loopback port. The endpoints only reach the document once the app has started.
        var config = new RunnerConfig { Dashboard = new DashboardConfig { Port = 0 } };
        var options = new DashboardAppOptions(
            config,
            new RunnerStatus("export", "repos.yml", TimeProvider.System),
            TimeProvider.System,
            WebRoot: "");
        var builder = DashboardApp.CreateBuilder(options);
        await using var app = DashboardApp.Build(builder, options);

        await app.StartAsync(cancellationToken);
        var json = await GetDocumentAsync(app.Services, cancellationToken);
        await app.StopAsync(cancellationToken);

        var fullPath = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(fullPath, json, cancellationToken);
    }

    /// <summary>The v1 document as JSON, read from the services of an app that has started (its routes are known then).</summary>
    public static async Task<string> GetDocumentAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var provider = services.GetRequiredKeyedService<IOpenApiDocumentProvider>(DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(cancellationToken);

        await using var writer = new StringWriter();
        var jsonWriter = new OpenApiJsonWriter(writer);
        document.SerializeAsV31(jsonWriter);

        return writer.ToString();
    }
}
