using AiRunner.Core;
using AiRunner.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using AgentWriteKind = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Accepted<AiRunner.Web.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.BadRequest<AiRunner.Web.AgentValidationError>,
    Microsoft.AspNetCore.Http.HttpResults.NotFound<AiRunner.Web.ApiError>,
    Microsoft.AspNetCore.Http.HttpResults.Conflict<AiRunner.Web.ApiError>>;

namespace AiRunner.Web;

/// <summary>One shared file. Codes are stable (for styling), labels are the text to show.</summary>
/// <param name="Kind"><c>agent</c>, <c>skill</c> or <c>template</c> (templates are read-only).</param>
/// <param name="CanRevert">Revert would change something: an editable file that differs from git HEAD.</param>
public sealed record AgentFileResponse(
    string Path,
    string Kind,
    string GitState,
    string GitStateLabel,
    string SyncState,
    string SyncStateLabel,
    bool IsEditable,
    bool CanRevert);

/// <param name="Extra">Files in ~/.copilot/ the repo does not have; they are never deleted.</param>
/// <param name="IsOutOfSync">A file differs or is not installed: the Sync banner shows.</param>
/// <param name="TemplatesNote">Why templates cannot be edited here.</param>
public sealed record AgentsResponse(
    IReadOnlyList<AgentFileResponse> Files,
    IReadOnlyList<string> Extra,
    bool IsOutOfSync,
    string TemplatesNote);

/// <param name="Hash">Send it back as <c>baseHash</c> when saving.</param>
/// <param name="Diff">Unified diff against git HEAD; empty when the file is unchanged.</param>
public sealed record AgentFileDetailResponse(AgentFileResponse File, string Text, string Hash, string Diff);

public sealed record SaveAgentRequest(string Text, string BaseHash);

/// <summary>A save that failed the file rules: the message and every reason.</summary>
public sealed record AgentValidationError(string Message, IReadOnlyList<string> Reasons);

/// <summary>
/// The agents and skills routes (docs/specs/dashboard-control.md section 7). The path is a query value, not a route
/// segment: it holds slashes, and a client would send them encoded. <see cref="AgentPaths"/> checks it in the runner.
/// </summary>
public static class AgentsApi
{
    private const string TemplatesNote =
        "Templates are shown read-only: a template change needs its parser, its agent file and Samples.cs changed " +
        "together (CLAUDE.md), which is a code change, not a dashboard edit.";

    public static void MapAgentsApi(RouteGroupBuilder api, RouteGroupBuilder writes)
    {
        api.MapGet("/agents", GetAgentsAsync)
            .WithTags("Agents")
            .WithName("getAgents")
            .WithSummary("The shared agents, skills and templates with their git state and sync state.");

        api.MapGet("/agents/file", GetFileAsync)
            .WithTags("Agents")
            .WithName("getAgentFile")
            .WithSummary("One file: its text, its hash and the diff against git HEAD.");

        writes.MapPut("/agents/file", SaveAsync)
            .WithTags("Agents")
            .WithName("saveAgentFile")
            .WithSummary(
                "Save an existing agent or skill file. 409 when it changed since it was read, "
                + "400 when it breaks the rules.");

        writes.MapPost("/agents/revert", RevertAsync)
            .WithTags("Agents")
            .WithName("revertAgentFile")
            .WithSummary("Restore one file from git HEAD.");

        writes.MapPost("/agents/sync", Sync)
            .WithTags("Agents")
            .WithName("syncAgents")
            .WithSummary("Copy the shared agents and skills to ~/.copilot between jobs (right after the running job).");
    }

    private static async Task<AgentsResponse> GetAgentsAsync(IRunnerAgents agents, CancellationToken cancellationToken)
    {
        var listing = await agents.ListAsync(cancellationToken);
        var files = listing.Files.Select(ToResponse).ToList();

        return new AgentsResponse(files, listing.Extra, listing.IsOutOfSync, TemplatesNote);
    }

    private static async Task<Results<Ok<AgentFileDetailResponse>, NotFound<ApiError>>> GetFileAsync(
        string path,
        IRunnerAgents agents,
        CancellationToken cancellationToken)
    {
        var detail = await agents.ReadAsync(path, cancellationToken);
        if (detail is null)
        {
            return TypedResults.NotFound(new ApiError(AgentPaths.NotSharedMessage));
        }

        return TypedResults.Ok(new AgentFileDetailResponse(
            ToResponse(detail.Info),
            detail.Text,
            detail.Hash,
            detail.Diff));
    }

    private static async Task<AgentWriteKind> SaveAsync(
        string path,
        SaveAgentRequest request,
        IRunnerAgents agents,
        CancellationToken cancellationToken)
    {
        var save = new AgentSave(path, request.Text, request.BaseHash);
        var reply = await agents.SaveAsync(save, cancellationToken);

        return AnswerOf(reply);
    }

    private static async Task<AgentWriteKind> RevertAsync(
        string path,
        IRunnerAgents agents,
        CancellationToken cancellationToken)
    {
        var reply = await agents.RevertAsync(path, cancellationToken);

        return AnswerOf(reply);
    }

    private static AgentWriteKind Sync(IRunnerAgents agents)
    {
        var reply = agents.RequestSync();

        return AnswerOf(reply);
    }

    private static AgentWriteKind AnswerOf(AgentReply reply)
    {
        var error = new ApiError(reply.Message);

        return reply.Outcome switch
        {
            AgentOutcome.Done => TypedResults.Accepted((string?)null, new CommandResponse(reply.Message)),
            AgentOutcome.NotFound => TypedResults.NotFound(error),
            AgentOutcome.Stale => TypedResults.Conflict(error),
            _ => TypedResults.BadRequest(new AgentValidationError(reply.Message, reply.Reasons)),
        };
    }

    private static AgentFileResponse ToResponse(AgentFileInfo info) => new(
        info.Path,
        info.Kind,
        info.GitState,
        AgentStates.LabelOf(info.GitState),
        info.SyncState,
        AgentStates.LabelOf(info.SyncState),
        info.IsEditable,
        info.CanRevert);
}
