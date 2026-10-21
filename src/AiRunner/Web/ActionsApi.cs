using AiRunner.Core;
using AiRunner.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using ActionResponseKind = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Accepted<AiRunner.Web.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.BadRequest<AiRunner.Web.ApiError>,
    Microsoft.AspNetCore.Http.HttpResults.NotFound<AiRunner.Web.ApiError>,
    Microsoft.AspNetCore.Http.HttpResults.Conflict<AiRunner.Web.ApiError>,
    Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<AiRunner.Web.ApiError>>;

namespace AiRunner.Web;

/// <summary>The body of an action: the text the user typed, for the actions that take one (answer, ask, fix).</summary>
public sealed record ActionRequest(string? Text);

/// <summary>The route values of <c>POST /api/issues|pulls/{owner}/{repo}/{number}/actions/{code}</c>.</summary>
public sealed record ActionRoute(
    [FromRoute(Name = "owner")] string Owner,
    [FromRoute(Name = "repo")] string Repo,
    [FromRoute(Name = "number")] int Number,
    [FromRoute(Name = "code")] string Code);

/// <summary>
/// The action routes (docs/specs/dashboard-control.md section 4). Each hands the item and the action to
/// <see cref="IRunnerActions"/>, which reads the item fresh, checks the action is still offered, writes a label or a
/// comment on the code host and wakes the poller. Nothing here touches the queue.
/// </summary>
public static class ActionsApi
{
    public static void MapActionsApi(RouteGroupBuilder writes)
    {
        writes.MapPost("/issues/{owner}/{repo}/{number:int}/actions/{code}", RunIssueActionAsync)
            .WithTags("Issues")
            .WithName("runIssueAction")
            .WithSummary("Run an action on an issue (a label or a comment on the code host); the planner picks it up.");

        writes.MapPost("/pulls/{owner}/{repo}/{number:int}/actions/{code}", RunPullActionAsync)
            .WithTags("Pulls")
            .WithName("runPullAction")
            .WithSummary("Run an action on a PR (full review, fix); the planner picks it up.");
    }

    private static async Task<ActionResponseKind> RunIssueActionAsync(
        [AsParameters] ActionRoute route,
        ActionRequest? request,
        IRunnerActions actions,
        CancellationToken cancellationToken)
    {
        var (owner, repo, number, code) = route;
        var call = new ActionCall(code, request?.Text);
        var result = await actions.RunIssueAsync(new IssueRef($"{owner}/{repo}", number), call, cancellationToken);

        return AnswerOf(result);
    }

    private static async Task<ActionResponseKind> RunPullActionAsync(
        [AsParameters] ActionRoute route,
        ActionRequest? request,
        IRunnerActions actions,
        CancellationToken cancellationToken)
    {
        var (owner, repo, number, code) = route;
        var call = new ActionCall(code, request?.Text);
        var result = await actions.RunPullAsync(new IssueRef($"{owner}/{repo}", number), call, cancellationToken);

        return AnswerOf(result);
    }

    private static ActionResponseKind AnswerOf(ActionReply result)
    {
        var error = new ApiError(result.Message);

        return result.Outcome switch
        {
            ActionOutcome.Done => TypedResults.Accepted((string?)null, new CommandResponse(result.Message)),
            ActionOutcome.NotFound => TypedResults.NotFound(error),
            ActionOutcome.NotOffered => TypedResults.Conflict(error),
            ActionOutcome.Failed => TypedResults.Json(error, statusCode: StatusCodes.Status502BadGateway),
            _ => TypedResults.BadRequest(error),
        };
    }
}

/// <summary>
/// The actions of a web app that runs without a runner (the OpenAPI export, tests of other routes): there is no code
/// host to write to, so every action is refused.
/// </summary>
public sealed class NoActions : IRunnerActions
{
    private static readonly ActionReply Refused =
        new(ActionOutcome.NotFound, "This dashboard has no runner to act through.");

    public static NoActions Instance { get; } = new();

    public Task<ActionReply> RunIssueAsync(IssueRef issue, ActionCall call, CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    public Task<ActionReply> RunPullAsync(IssueRef pull, ActionCall call, CancellationToken cancellationToken) =>
        Task.FromResult(Refused);
}
