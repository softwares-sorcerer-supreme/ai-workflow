using System.Globalization;
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

        api.MapGet("/issues", GetIssues)
            .WithTags("Issues")
            .WithName("getIssues")
            .WithSummary("Every issue the runner polls, with its stage; sub-issues nested unless flat.");

        api.MapGet("/issues/{owner}/{repo}/{number:int}", GetIssue)
            .WithTags("Issues")
            .WithName("getIssue")
            .WithSummary("One issue: stage, what it waits on, analysis summary, sub-issues, PRs and job timeline.");

        api.MapGet("/cost", GetCost)
            .WithTags("Cost")
            .WithName("getCost")
            .WithSummary("Copilot calls, jobs and job time per day, project or issue (with its sub-issues).");

        return endpoints;
    }

    /// <summary>The query string of <c>GET /api/cost</c>.</summary>
    public sealed record CostFilter(
        [FromQuery(Name = "by")] string? By,
        [FromQuery(Name = "from")] string? From,
        [FromQuery(Name = "to")] string? To);

    private static Results<Ok<CostResponse>, BadRequest<ApiError>> GetCost(
        [AsParameters] CostFilter filter,
        CostReader cost)
    {
        var (by, from, to) = filter;

        var grouping = CostGrouping.Day;
        if (by is not null && !(Enum.TryParse(by, ignoreCase: true, out grouping) && Enum.IsDefined(grouping)))
        {
            return TypedResults.BadRequest(new ApiError($"Unknown grouping '{by}'. Use day, project or issue."));
        }

        if (!TryParseDay(from, out var fromDay) || !TryParseDay(to, out var toDay))
        {
            return TypedResults.BadRequest(new ApiError($"Dates are written {CostReader.DateFormat}."));
        }

        if (fromDay > toDay)
        {
            return TypedResults.BadRequest(new ApiError("'from' is after 'to'."));
        }

        var query = new CostQuery(grouping, new CostRange(fromDay, toDay));
        var report = cost.Read(query);

        return TypedResults.Ok(report);
    }

    /// <summary>A missing day is fine (open end); a present one must be a real calendar day.</summary>
    private static bool TryParseDay(string? text, out DateOnly? day)
    {
        day = null;
        if (text is null)
        {
            return true;
        }

        var isDay = DateOnly.TryParseExact(
            text, CostReader.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed);
        if (isDay)
        {
            day = parsed;
        }

        return isDay;
    }

    /// <summary>The route values of <c>GET /api/issues/{owner}/{repo}/{number}</c>.</summary>
    public sealed record IssueRoute(
        [FromRoute(Name = "owner")] string Owner,
        [FromRoute(Name = "repo")] string Repo,
        [FromRoute(Name = "number")] int Number);

    private static Results<Ok<IssueDetailResponse>, NotFound> GetIssue(
        [AsParameters] IssueRoute route,
        IssueDetailReader detail)
    {
        var (owner, repo, number) = route;
        var found = detail.Read($"{owner}/{repo}", number);

        if (found is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(found);
    }

    /// <summary>The query string of <c>GET /api/issues</c>.</summary>
    public sealed record IssuesFilter(
        [FromQuery(Name = "project")] string? Project,
        [FromQuery(Name = "component")] string? Component,
        [FromQuery(Name = "stage")] string? Stage,
        [FromQuery(Name = "state")] string? State,
        [FromQuery(Name = "flat")] bool? Flat);

    private static Results<Ok<IssueListResponse>, BadRequest<ApiError>> GetIssues(
        [AsParameters] IssuesFilter filter,
        RunnerConfig config,
        IssueReader issues)
    {
        var (project, component, stage, state, flat) = filter;

        ProjectConfig? found = null;
        if (project is not null)
        {
            found = config.ProjectNamed(project);
            if (found is null)
            {
                return TypedResults.BadRequest(UnknownProject(project));
            }
        }

        var projects = found is null ? config.Projects : [found];
        if (component is not null && !projects.Any(candidate => candidate.CodeComponentNames.Contains(component)))
        {
            return TypedResults.BadRequest(new ApiError($"Unknown component '{component}'."));
        }

        if (stage is not null && !IssueStage.Stages.Any(known => known.Code == stage))
        {
            return TypedResults.BadRequest(new ApiError($"Unknown stage '{stage}'."));
        }

        if (state is not null && state != SnapshotRules.Open && state != SnapshotRules.Closed)
        {
            return TypedResults.BadRequest(new ApiError($"Unknown state '{state}'. Use open or closed."));
        }

        var query = new IssueQuery(project, component, stage, state, flat ?? false);
        var list = issues.Read(query);

        return TypedResults.Ok(list);
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
            var found = config.ProjectNamed(project);
            if (found is null)
            {
                return TypedResults.BadRequest(UnknownProject(project));
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

        var response = JobResponse.From(job, config);

        return TypedResults.Ok(response);
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

        return TypedResults.Ok(new LogFileTextResponse(
            content.Name,
            FormatOf(content.Name),
            content.SizeBytes,
            content.Text,
            content.IsTruncated,
            note));
    }

    private static ApiError UnknownProject(string project) => new($"Unknown project '{project}'.");

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
