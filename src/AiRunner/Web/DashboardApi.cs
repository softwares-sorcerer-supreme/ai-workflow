using AiRunner.Configuration;
using AiRunner.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AiRunner.Web;

/// <summary>
/// The dashboard's JSON API under <c>/api</c>: read-only, computed answers only (codes plus labels), UTC times.
/// It reads the runner's in-memory status and local data; it never calls GitBucket (docs/adr/0001).
/// </summary>
public static class DashboardApi
{
    public static IEndpointRouteBuilder MapDashboardApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // The tag groups the generated client functions (api/fn/<tag>/...); the name is the function name.
        api.MapGet("/health", GetHealth)
            .WithTags("Health")
            .WithName("getHealth")
            .WithSummary("Runner health: last poll, poll errors, worker idle or busy.");

        return endpoints;
    }

    private static HealthResponse GetHealth(RunnerStatus status, RunnerConfig config, TimeProvider time)
    {
        var snapshot = status.Snapshot();
        var now = time.GetUtcNow();

        return HealthResponse.From(snapshot, config, now);
    }
}
