using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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

        api.MapGet("/overview", GetOverview)
            .WithTags("Overview")
            .WithName("getOverview")
            .WithSummary("The running job and the queue.");

        api.MapGet("/jobs", GetJobs)
            .WithTags("Jobs")
            .WithName("getJobs")
            .WithSummary("Job history, newest first, one page at a time.");

        api.MapGet("/jobs/{id:long}", GetJob)
            .WithTags("Jobs")
            .WithName("getJob")
            .WithSummary("One job with its result.");

        api.MapGet("/jobs/{id:long}/files", GetJobFiles)
            .WithTags("Jobs")
            .WithName("getJobFiles")
            .WithSummary("The files in a job's log folder.");

        api.MapGet("/jobs/{id:long}/files/{name}", GetJobFile)
            .WithTags("Jobs")
            .WithName("getJobFile")
            .WithSummary("One log file as text (cut after 2 MB).");

        return endpoints;
    }

    private const int PageSize = 25;

    private static OverviewResponse GetOverview(OverviewReader overview) => overview.Read();

    /// <summary>The query string of <c>GET /api/jobs</c>.</summary>
    public sealed record JobsFilter(
        [FromQuery(Name = "status")] string? Status,
        [FromQuery(Name = "type")] string? Type,
        [FromQuery(Name = "project")] string? Project,
        [FromQuery(Name = "page")] int? Page);

    /// <summary>The route values of <c>GET /api/jobs/{id}/files/{name}</c>.</summary>
    public sealed record JobFileRoute([FromRoute(Name = "id")] long Id, [FromRoute(Name = "name")] string Name);

    private static Results<Ok<JobsPageResponse>, BadRequest<ApiError>> GetJobs(
        [AsParameters] JobsFilter filter,
        RunnerConfig config,
        IJobReader jobs)
    {
        var (status, type, project, page) = filter;

        if (status is not null && !JobStatuses.Contains(status))
        {
            return TypedResults.BadRequest(new ApiError($"Unknown status '{status}'."));
        }

        if (type is not null && !Enum.GetNames<JobType>().Contains(type))
        {
            return TypedResults.BadRequest(new ApiError($"Unknown job type '{type}'."));
        }

        IReadOnlyList<string>? repos = null;
        if (project is not null)
        {
            var found = config.Projects.FirstOrDefault(candidate => candidate.Name == project);
            if (found is null)
            {
                return TypedResults.BadRequest(new ApiError($"Unknown project '{project}'."));
            }

            repos = found.Repos.ToList();
        }

        var pageNumber = Math.Max(1, page ?? 1);
        var query = new JobQuery(status, type, repos, pageNumber, PageSize);
        var result = jobs.QueryHistory(query);
        var items = result.Items
            .Select(job => JobResponse.From(job, config))
            .ToList();
        var totalPages = (result.Total + PageSize - 1) / PageSize;

        return TypedResults.Ok(new JobsPageResponse(items, pageNumber, PageSize, result.Total, totalPages));
    }

    private static Results<Ok<JobResponse>, NotFound> GetJob(long id, RunnerConfig config, IJobReader jobs)
    {
        var job = jobs.GetRecord(id);
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(JobResponse.From(job, config));
    }

    private static Results<Ok<LogFilesResponse>, NotFound> GetJobFiles(long id, IJobReader jobs, LogViewer logs)
    {
        var job = jobs.GetRecord(id);
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        var files = logs.List(job)
            .Select(file => new LogFileResponse(file.Name, FormatOf(file.Name), file.SizeBytes))
            .ToList();

        return TypedResults.Ok(new LogFilesResponse(id, files));
    }

    private static Results<Ok<LogFileTextResponse>, BadRequest<ApiError>, NotFound> GetJobFile(
        [AsParameters] JobFileRoute route,
        IJobReader jobs,
        LogViewer logs)
    {
        var (id, name) = route;
        var job = jobs.GetRecord(id);
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        var result = logs.Read(job, name);
        if (result.Status == LogReadStatus.BadName)
        {
            return TypedResults.BadRequest(new ApiError("The file name must be a plain file name."));
        }

        if (result.Content is not { } content)
        {
            return TypedResults.NotFound();
        }

        var note = content.IsTruncated ? "Showing the first 2 MB of this file." : null;
        var text = content.IsTruncated ? content.Text + "\n\n[... cut after 2 MB]" : content.Text;

        return TypedResults.Ok(new LogFileTextResponse(
            content.Name,
            FormatOf(content.Name),
            content.SizeBytes,
            text,
            content.IsTruncated,
            note));
    }

    private static string FormatOf(string fileName) =>
        fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "markdown" : "text";

    private static readonly string[] JobStatuses =
        [JobStatus.Pending, JobStatus.Running, JobStatus.Done, JobStatus.Failed];

    private static HealthResponse GetHealth(RunnerStatus status, RunnerConfig config, TimeProvider time)
    {
        var snapshot = status.Snapshot();
        var now = time.GetUtcNow();

        return HealthResponse.From(snapshot, config, now);
    }
}
