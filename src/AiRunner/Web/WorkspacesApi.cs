using AiRunner.Hosting;
using AiRunner.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using WorkspaceActionKind = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Accepted<AiRunner.Web.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.BadRequest<AiRunner.Web.ApiError>,
    Microsoft.AspNetCore.Http.HttpResults.NotFound<AiRunner.Web.ApiError>,
    Microsoft.AspNetCore.Http.HttpResults.Conflict<AiRunner.Web.ApiError>,
    Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<AiRunner.Web.ApiError>>;

namespace AiRunner.Web;

public sealed record WarningResponse(string Code, string Message);

/// <summary>One clone. Times are UTC; <c>SizeBytes</c> is null until the background walk of the folder is done.</summary>
public sealed record CloneResponse(
    string Repo,
    string Folder,
    bool Exists,
    IReadOnlyList<string> Components,
    string? HeadSha,
    string? HeadSubject,
    DateTimeOffset? HeadAt,
    string? Branch,
    string? DetachedAt,
    DateTimeOffset? LastFetchAt,
    IReadOnlyList<string> ChangedPaths,
    long? SizeBytes,
    IReadOnlyList<WarningResponse> Warnings);

/// <param name="IsBusy">A job or an action holds the project, so a fetch or re-clone would be refused.</param>
public sealed record ProjectWorkspaceResponse(string Name, bool IsBusy, IReadOnlyList<CloneResponse> Clones);

public sealed record OrphanResponse(string Folder, long? SizeBytes);

public sealed record WorkspacesResponse(
    IReadOnlyList<ProjectWorkspaceResponse> Projects,
    IReadOnlyList<OrphanResponse> Orphans);

/// <summary>
/// The workspace routes (docs/specs/dashboard-control.md section 6). The list reads local git only, never the network.
/// The actions go to <see cref="IRunnerWorkspaces"/>, which refuses with 409 while a job holds the project.
/// <c>{repo}</c> is the clone's folder name (a repo name has a slash, so it cannot be one route segment).
/// </summary>
public static class WorkspacesApi
{
    public static void MapWorkspacesApi(RouteGroupBuilder api, RouteGroupBuilder writes)
    {
        api.MapGet("/workspaces", GetWorkspacesAsync)
            .WithTags("Workspaces")
            .WithName("getWorkspaces")
            .WithSummary("Every clone under work/ per project with its warnings, and the folders no project owns.");

        writes.MapPost("/workspaces/{project}/{repo}/fetch", FetchAsync)
            .WithTags("Workspaces")
            .WithName("fetchWorkspace")
            .WithSummary("git fetch of one clone. 409 while a job uses the project.");

        writes.MapPost("/workspaces/{project}/{repo}/reclone", RecloneAsync)
            .WithTags("Workspaces")
            .WithName("recloneWorkspace")
            .WithSummary("Delete one clone; the next job clones it again. 409 while a job uses the project.");

        writes.MapDelete("/workspaces/orphans/{folder}", DeleteOrphanAsync)
            .WithTags("Workspaces")
            .WithName("deleteOrphanWorkspace")
            .WithSummary("Delete a folder of work/ that belongs to no project (a plain folder name only).");
    }

    private static async Task<WorkspacesResponse> GetWorkspacesAsync(
        IRunnerWorkspaces workspaces,
        CancellationToken cancellationToken)
    {
        var listing = await workspaces.ListAsync(cancellationToken);

        var projects = listing.Projects
            .Select(project => new ProjectWorkspaceResponse(
                project.Name,
                project.IsBusy,
                project.Clones.Select(ToResponse).ToList()))
            .ToList();
        var orphans = listing.Orphans
            .Select(orphan => new OrphanResponse(orphan.Folder, orphan.SizeBytes))
            .ToList();

        return new WorkspacesResponse(projects, orphans);
    }

    private static CloneResponse ToResponse(CloneInfo clone)
    {
        var head = clone.Head;
        var warnings = clone.Warnings
            .Select(warning => new WarningResponse(warning.Code, warning.Message))
            .ToList();

        return new CloneResponse(
            clone.Repo,
            clone.Folder,
            clone.Exists,
            clone.Components,
            head?.Sha,
            head?.Subject,
            head?.CommittedAt.ToUniversalTime(),
            head?.Branch,
            head?.DetachedAt,
            clone.LastFetchAt,
            clone.ChangedPaths,
            clone.SizeBytes,
            warnings);
    }

    private static async Task<WorkspaceActionKind> FetchAsync(
        string project,
        string repo,
        IRunnerWorkspaces workspaces,
        CancellationToken cancellationToken)
    {
        var reply = await workspaces.FetchAsync(project, repo, cancellationToken);

        return AnswerOf(reply);
    }

    private static async Task<WorkspaceActionKind> RecloneAsync(
        string project,
        string repo,
        IRunnerWorkspaces workspaces,
        CancellationToken cancellationToken)
    {
        var reply = await workspaces.RecloneAsync(project, repo, cancellationToken);

        return AnswerOf(reply);
    }

    private static async Task<WorkspaceActionKind> DeleteOrphanAsync(
        string folder,
        IRunnerWorkspaces workspaces,
        CancellationToken cancellationToken)
    {
        var reply = await workspaces.DeleteOrphanAsync(folder, cancellationToken);

        return AnswerOf(reply);
    }

    private static WorkspaceActionKind AnswerOf(WorkspaceReply reply)
    {
        var error = new ApiError(reply.Message);

        return reply.Outcome switch
        {
            WorkspaceOutcome.Done => TypedResults.Accepted((string?)null, new CommandResponse(reply.Message)),
            WorkspaceOutcome.NotFound => TypedResults.NotFound(error),
            WorkspaceOutcome.Busy => TypedResults.Conflict(error),
            WorkspaceOutcome.Failed => TypedResults.Json(error, statusCode: StatusCodes.Status502BadGateway),
            _ => TypedResults.BadRequest(error),
        };
    }
}
