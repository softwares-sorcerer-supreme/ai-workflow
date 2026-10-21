using AiRunner.Hosting;
using AiRunner.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AiRunner.Web;

/// <summary>
/// <c>GET /api/setup</c>: where each secret comes from and whether it is set; never a secret. <c>TokenSource</c> is
/// <c>user-secrets | environment | none</c>, <c>WebPasswordSource</c> <c>user-secrets | repos-yml | none</c>.
/// <c>CanRemove*</c> say whether the Remove button applies (only a user secret can be removed here). Each source
/// comes with its label and, when the value lives outside user secrets, a note saying where it is changed.
/// </summary>
/// <param name="Login">The token's user on the code host; empty while there is no accepted token.</param>
/// <param name="BaseUrl">code_host.base_url, read only here (it is changed in repos.yml).</param>
/// <param name="SecretsFile">The user-secrets file the page saves to.</param>
public sealed record SetupResponse(
    bool IsTokenSet,
    string TokenSource,
    string TokenSourceLabel,
    string? TokenNote,
    string Login,
    string BaseUrl,
    bool IsWebPasswordSet,
    string WebPasswordSource,
    string WebPasswordSourceLabel,
    string? WebPasswordNote,
    string SecretsFile,
    bool CanRemoveToken,
    bool CanRemoveWebPassword)
{
    public static SetupResponse From(SetupState state) => new(
        state.IsTokenSet,
        state.TokenSource,
        SourceLabelOf(state.TokenSource),
        TokenNoteOf(state.TokenSource),
        state.Login,
        state.BaseUrl,
        state.IsWebPasswordSet,
        state.WebPasswordSource,
        SourceLabelOf(state.WebPasswordSource),
        WebPasswordNoteOf(state.WebPasswordSource),
        state.SecretsFile,
        state.CanRemoveToken,
        state.CanRemoveWebPassword);

    private static string SourceLabelOf(string source) => source switch
    {
        CredentialSource.UserSecrets => "User secrets",
        CredentialSource.Environment => "Environment variable",
        CredentialSource.ReposYml => "repos.yml",
        _ => "Not set",
    };

    private static string? TokenNoteOf(string source) => source == CredentialSource.Environment
        ? "This token comes from the GITBUCKET_TOKEN (or GitBucket__Token) environment variable and can only be " +
          "changed or removed there. A token saved here is used instead of it."
        : null;

    private static string? WebPasswordNoteOf(string source) => source == CredentialSource.ReposYml
        ? "This password comes from code_host.web_password in repos.yml and can only be changed or removed there. " +
          "A password saved here is used instead of it."
        : null;
}

/// <summary>The answer of a setup change: the message to show and the setup as it is now.</summary>
public sealed record SetupChangeResponse(string Message, SetupResponse Setup);

/// <summary>The body of <c>PUT /api/setup/token</c>.</summary>
public sealed record TokenRequest(string Token);

/// <summary>The body of <c>PUT /api/setup/web-password</c>.</summary>
public sealed record WebPasswordRequest(string Password);

/// <summary>
/// The Setup page's routes (docs/specs/dashboard-control.md section 3). Each write hands the secret to
/// <see cref="IRunnerSetup"/>, which checks it against the code host before it saves it; no answer carries a secret,
/// and nothing here logs a request body.
/// </summary>
public static class SetupApi
{
    public static void MapSetupApi(RouteGroupBuilder api, RouteGroupBuilder writes)
    {
        api.MapGet("/setup", GetSetup)
            .WithTags("Setup")
            .WithName("getSetup")
            .WithSummary("Where the token and the web password come from, the login and the secrets file; no secret.");

        writes.MapPut("/setup/token", SaveTokenAsync)
            .WithTags("Setup")
            .WithName("saveToken")
            .WithSummary("Check the token against the code host, save it in user secrets and use it now.");

        writes.MapDelete("/setup/token", RemoveTokenAsync)
            .WithTags("Setup")
            .WithName("removeToken")
            .WithSummary("Remove the token from user secrets; a token from the environment cannot be removed here.");

        writes.MapPut("/setup/web-password", SaveWebPasswordAsync)
            .WithTags("Setup")
            .WithName("saveWebPassword")
            .WithSummary("Check the web password with a sign-in, save it in user secrets and use it now.");

        writes.MapDelete("/setup/web-password", RemoveWebPasswordAsync)
            .WithTags("Setup")
            .WithName("removeWebPassword")
            .WithSummary("Remove the web password from user secrets; one in repos.yml cannot be removed here.");
    }

    private static Ok<SetupResponse> GetSetup(IRunnerSetup setup)
    {
        var state = setup.Read();
        var response = SetupResponse.From(state);

        return TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<SetupChangeResponse>, BadRequest<ApiError>, Conflict<ApiError>>>
        SaveTokenAsync(TokenRequest request, IRunnerSetup setup, CancellationToken cancellationToken)
    {
        var result = await setup.SaveTokenAsync(request.Token ?? "", cancellationToken);

        return AnswerOf(result, setup);
    }

    private static async Task<Results<Ok<SetupChangeResponse>, BadRequest<ApiError>, Conflict<ApiError>>>
        RemoveTokenAsync(IRunnerSetup setup, CancellationToken cancellationToken)
    {
        var result = await setup.RemoveTokenAsync(cancellationToken);

        return AnswerOf(result, setup);
    }

    private static async Task<Results<Ok<SetupChangeResponse>, BadRequest<ApiError>, Conflict<ApiError>>>
        SaveWebPasswordAsync(WebPasswordRequest request, IRunnerSetup setup, CancellationToken cancellationToken)
    {
        var result = await setup.SaveWebPasswordAsync(request.Password ?? "", cancellationToken);

        return AnswerOf(result, setup);
    }

    private static async Task<Results<Ok<SetupChangeResponse>, BadRequest<ApiError>, Conflict<ApiError>>>
        RemoveWebPasswordAsync(IRunnerSetup setup, CancellationToken cancellationToken)
    {
        var result = await setup.RemoveWebPasswordAsync(cancellationToken);

        return AnswerOf(result, setup);
    }

    private static Results<Ok<SetupChangeResponse>, BadRequest<ApiError>, Conflict<ApiError>> AnswerOf(
        SetupResult result,
        IRunnerSetup setup)
    {
        switch (result.Outcome)
        {
            case SetupOutcome.Refused:
                return TypedResults.BadRequest(new ApiError(result.Message));
            case SetupOutcome.Conflict:
                return TypedResults.Conflict(new ApiError(result.Message));
            default:
                var state = setup.Read();
                var setupResponse = SetupResponse.From(state);

                return TypedResults.Ok(new SetupChangeResponse(result.Message, setupResponse));
        }
    }
}

/// <summary>
/// The setup of a web app that runs without a runner (the OpenAPI export, tests of other routes): it reports a token
/// as set, so the health is not "needs-setup", and refuses every change.
/// </summary>
public sealed class NoSetup : IRunnerSetup
{
    private const string Message = "This dashboard has no runner to set up.";

    public static NoSetup Instance { get; } = new();

    public SetupState Read() => new(
        IsTokenSet: true,
        TokenSource: CredentialSource.None,
        Login: "",
        BaseUrl: "",
        IsWebPasswordSet: false,
        WebPasswordSource: CredentialSource.None,
        SecretsFile: "");

    public Task<SetupResult> SaveTokenAsync(string token, CancellationToken cancellationToken) => Refused();

    public Task<SetupResult> RemoveTokenAsync(CancellationToken cancellationToken) => Refused();

    public Task<SetupResult> SaveWebPasswordAsync(string password, CancellationToken cancellationToken) => Refused();

    public Task<SetupResult> RemoveWebPasswordAsync(CancellationToken cancellationToken) => Refused();

    private static Task<SetupResult> Refused() => Task.FromResult(new SetupResult(SetupOutcome.Conflict, Message));
}
