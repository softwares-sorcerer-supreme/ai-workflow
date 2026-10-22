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
/// The dashboard's JSON API under <c>/api</c>: computed answers only (codes plus labels), UTC times.
/// It reads the runner's in-memory status and local data; it never calls GitBucket (docs/adr/0001). The writes are
/// runtime controls handed to <see cref="RunnerCommands"/> and setup changes handed to <see cref="IRunnerSetup"/>
/// (<see cref="SetupApi"/>); each is marked <see cref="WriteRoute"/>, so it is behind the <see cref="WriteGuard"/>.
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
            .WithSummary("Copilot calls, billed credits, tokens, jobs and job time per day, project, issue or model.");

        var writes = api.MapGroup("")
            .WithMetadata(WriteRoute.Instance);

        writes.MapPost("/runner/poll", PollNow)
            .WithTags("Runner")
            .WithName("pollNow")
            .WithSummary("Poll now; a poll that is running finishes first.");

        writes.MapPost("/runner/pause", Pause)
            .WithTags("Runner")
            .WithName("pauseRunner")
            .WithSummary("Take no new job until resumed; polls go on and the running job finishes.");

        writes.MapPost("/runner/resume", Resume)
            .WithTags("Runner")
            .WithName("resumeRunner")
            .WithSummary("Take jobs again after a pause.");

        writes.MapPost("/jobs/{id:long}/cancel", CancelJob)
            .WithTags("Jobs")
            .WithName("cancelJob")
            .WithSummary("Cancel the running job; it fails like a timeout. A pending job cannot be cancelled.");

        SetupApi.MapSetupApi(api, writes);
        ActionsApi.MapActionsApi(writes);
        WorkspacesApi.MapWorkspacesApi(api, writes);
        AgentsApi.MapAgentsApi(api, writes);
        SettingsApi.MapSettingsApi(api, writes);
        ModelOptionsApi.MapModelOptionsApi(api);

        return endpoints;
    }

    private static Accepted<CommandResponse> PollNow(RunnerCommands commands)
    {
        commands.WakePoller();

        return AcceptedWith("A poll starts now, or right after the one that is running.");
    }

    private static Accepted<CommandResponse> Pause(RunnerCommands commands)
    {
        commands.Pause();

        return AcceptedWith("Paused: no new job starts. A running job finishes.");
    }

    private static Accepted<CommandResponse> Resume(RunnerCommands commands)
    {
        commands.Resume();

        return AcceptedWith("Resumed: queued jobs start again.");
    }

    private static Results<Accepted<CommandResponse>, Conflict<ApiError>> CancelJob(long id, RunnerCommands commands)
    {
        var isCancelled = commands.TryCancel(id);
        if (!isCancelled)
        {
            return TypedResults.Conflict(new ApiError($"Job {id} is not running. Pause the runner to hold queued jobs."));
        }

        return AcceptedWith($"Job {id} is being cancelled. Its issue or PR gets the failure comment.");
    }

    private static Accepted<CommandResponse> AcceptedWith(string message) =>
        TypedResults.Accepted((string?)null, new CommandResponse(message));

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
            return TypedResults.BadRequest(new ApiError($"Unknown grouping '{by}'. Use day, project, issue or model."));
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

    /// <summary>
    /// The query string of <c>GET /api/issues</c>. <c>Search</c> (<c>q</c>) is part of the title or the issue number
    /// ("12" or "#12"); <c>State</c> is <c>open</c>, <c>closed</c> or <c>all</c> (the same as none); <c>Sort</c> is
    /// <c>number</c> (default, newest first) or <c>updated</c> (last updated first).
    /// </summary>
    public sealed record IssuesFilter(
        [FromQuery(Name = "project")] string? Project,
        [FromQuery(Name = "component")] string? Component,
        [FromQuery(Name = "stage")] string? Stage,
        [FromQuery(Name = "state")] string? State,
        [FromQuery(Name = "flat")] bool? Flat,
        [FromQuery(Name = "lane")] string? Lane,
        [FromQuery(Name = "focus")] string? Focus,
        [FromQuery(Name = "q")] string? Search,
        [FromQuery(Name = "sort")] string? Sort);

    private static Results<Ok<IssueListResponse>, BadRequest<ApiError>> GetIssues(
        [AsParameters] IssuesFilter filter,
        RunnerConfig config,
        IssueReader issues)
    {
        var problem = WhyBadIssuesFilter(filter, config);

        if (problem is not null)
        {
            return TypedResults.BadRequest(new ApiError(problem));
        }

        var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();
        var state = filter.State == IssueReader.AllStates ? null : filter.State;
        var query = new IssueQuery(
            Project: filter.Project,
            Component: filter.Component,
            Stage: filter.Stage,
            State: state,
            Lane: filter.Lane,
            Focus: filter.Focus,
            Search: search,
            IsFlat: filter.Flat ?? false,
            IsByUpdate: filter.Sort == IssueReader.SortByUpdate);
        var list = issues.Read(query);

        return TypedResults.Ok(list);
    }

    /// <summary>Why the issue filters cannot be used (a value no option has), or null when they can.</summary>
    private static string? WhyBadIssuesFilter(IssuesFilter filter, RunnerConfig config)
    {
        ProjectConfig? found = null;
        if (filter.Project is { } project)
        {
            found = config.ProjectNamed(project);
            if (found is null)
            {
                return UnknownProject(project).Message;
            }
        }

        var projects = found is null ? config.Projects : [found];
        if (filter.Component is { } component &&
            !projects.Any(candidate => candidate.CodeComponentNames.Contains(component)))
        {
            return $"Unknown component '{component}'.";
        }

        if (filter.Stage is { } stage && !IssueStage.Stages.Any(known => known.Code == stage))
        {
            return $"Unknown stage '{stage}'.";
        }

        if (filter.State is { } state && !IssueReader.States.Any(known => known.Code == state))
        {
            return $"Unknown state '{state}'. Use open, closed or all.";
        }

        if (filter.Lane is { } lane && !IssueLane.Lanes.Any(known => known.Code == lane))
        {
            return $"Unknown lane '{lane}'.";
        }

        if (filter.Focus is { } focus && !IssueFocus.Focuses.Any(known => known.Code == focus))
        {
            return $"Unknown focus '{focus}'.";
        }

        if (filter.Sort is { } sort && !IssueReader.Sorts.Any(known => known.Code == sort))
        {
            return $"Unknown sort '{sort}'. Use number or updated.";
        }

        return null;
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
    public sealed record JobFileRoute(
        [FromRoute(Name = "id")] long Id,
        [FromRoute(Name = "name")] string Name,
        [FromQuery(Name = "offset")] long? Offset);

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
        var steps = JobStepResponse.All(jobs.ListSteps(id), response.IsRunning);
        response = response with { Steps = steps };

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
            .Select(file => new LogFileResponse(
                file.Name,
                FormatOf(file.Name),
                file.SizeBytes,
                LogFileRules.IsLiveName(file.Name)))
            .ToList();

        return TypedResults.Ok(new LogFilesResponse(id, files));
    }

    private static Results<Ok<LogFileTextResponse>, BadRequest<ApiError>, NotFound> GetJobFile(
        [AsParameters] JobFileRoute route,
        IJobReader jobs,
        LogViewer logs)
    {
        var (id, name, offset) = route;
        if (offset is < 0)
        {
            return TypedResults.BadRequest(new ApiError("The offset must not be negative."));
        }

        var job = jobs.GetRecord(id);
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        var result = logs.Read(job, name, offset ?? 0);
        if (result.Status == LogReadStatus.BadName)
        {
            return TypedResults.BadRequest(new ApiError("The file name must be a plain file name."));
        }

        if (result.Content is not { } content)
        {
            return TypedResults.NotFound();
        }

        var note = content.IsTruncated
            ? content.Offset == 0
                ? "Showing the first 2 MB of this file."
                : "Showing 2 MB from this offset; read again from the next offset for the rest."
            : null;

        return TypedResults.Ok(new LogFileTextResponse(
            content.Name,
            FormatOf(content.Name),
            content.SizeBytes,
            content.Text,
            content.IsTruncated,
            note,
            content.Offset,
            content.NextOffset));
    }

    private static ApiError UnknownProject(string project) => new($"Unknown project '{project}'.");

    private static string FormatOf(string fileName) =>
        fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "markdown" : "text";

    private static readonly string[] JobStatuses =
        [JobStatus.Pending, JobStatus.Running, JobStatus.Done, JobStatus.Failed];

    private static HealthResponse GetHealth(HealthReader health) => health.Read();
}
