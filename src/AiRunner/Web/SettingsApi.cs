using System.Text.Json.Nodes;
using AiRunner.Configuration;
using AiRunner.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SettingsAnswer = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<AiRunner.Web.SettingsChangeResponse>,
    Microsoft.AspNetCore.Http.HttpResults.BadRequest<AiRunner.Web.ApiError>,
    Microsoft.AspNetCore.Http.HttpResults.Conflict<AiRunner.Web.ApiError>>;

namespace AiRunner.Web;

/// <summary><c>GET /api/settings/schema</c>: every key of repos.yml; the Settings screen is built from it.</summary>
public sealed record SettingsSchemaResponse(IReadOnlyList<SettingDefinition> Settings);

/// <summary>
/// <c>GET /api/settings</c>: repos.yml as JSON with snake_case keys as in the file (only what is written; a secret is
/// <c>{ "isSet": true|false }</c>), the <c>Hash</c> a save sends back as <c>baseHash</c>, and per project the merged
/// value of every setting with its source (<c>project | defaults | default</c>). <c>Error</c> says why the file on
/// disk is not applied (then <c>File</c> is the last good one); <c>RestartNeeded</c> lists keys saved but applied
/// only at the next start; <c>HasComments</c> asks for the warning that a save drops the comments.
/// </summary>
public sealed record SettingsResponse(
    JsonObject? File,
    string Hash,
    int Version,
    string Path,
    string? Error,
    bool HasComments,
    IReadOnlyList<string> RestartNeeded,
    IReadOnlyList<ProjectEffective> Effective)
{
    public static SettingsResponse From(SettingsView view) => new(
        view.File,
        view.Hash,
        view.Version,
        view.Path,
        view.Error,
        view.HasComments,
        view.RestartNeeded,
        view.Effective);
}

/// <summary>
/// The body of <c>PUT /api/settings</c>: the whole edited file and the hash it was read with. A secret sent as null
/// (or as its <c>{ isSet }</c> mask) keeps its value, one sent as text is refused (passwords are saved on the Setup
/// page); any other null means "not written" (inherited).
/// </summary>
public sealed record SettingsRequest(JsonObject File, string BaseHash);

/// <summary>The answer of a save: the message to show and the settings as they are now.</summary>
public sealed record SettingsChangeResponse(string Message, SettingsResponse Settings);

/// <summary>
/// The Settings screen's routes (docs/specs/dashboard-control.md section 8). The token is not a setting: it has no
/// key here and is set on the Setup page. A save is a write route behind the guard; editable commands run on this
/// machine, which is acceptable only because the dashboard is loopback-only.
/// </summary>
public static class SettingsApi
{
    public static void MapSettingsApi(RouteGroupBuilder api, RouteGroupBuilder writes)
    {
        api.MapGet("/settings/schema", GetSchema)
            .WithTags("Settings")
            .WithName("getSettingsSchema")
            .WithSummary("Every key of repos.yml: path, scope, type, allowed values, help, default, restart, secret.");

        api.MapGet("/settings", GetSettings)
            .WithTags("Settings")
            .WithName("getSettings")
            .WithSummary("repos.yml as JSON (no secret), its hash, and the merged settings of each project.");

        writes.MapPut("/settings", Save)
            .WithTags("Settings")
            .WithName("saveSettings")
            .WithSummary("Check, back up, write and apply repos.yml; 409 when it changed since it was read.");
    }

    private static Ok<SettingsSchemaResponse> GetSchema() =>
        TypedResults.Ok(new SettingsSchemaResponse(SettingsSchema.All));

    private static Ok<SettingsResponse> GetSettings(IRunnerSettings settings)
    {
        var view = settings.Read();
        var response = SettingsResponse.From(view);

        return TypedResults.Ok(response);
    }

    private static SettingsAnswer Save(SettingsRequest request, IRunnerSettings settings)
    {
        if (request.File is null || string.IsNullOrEmpty(request.BaseHash))
        {
            return TypedResults.BadRequest(new ApiError("Send the whole file and the hash it was read with."));
        }

        var save = new SettingsSave(request.File, request.BaseHash);
        var result = settings.Save(save);

        switch (result.Outcome)
        {
            case SettingsSaveOutcome.Invalid:
                return TypedResults.BadRequest(new ApiError(result.Message));
            case SettingsSaveOutcome.Stale:
                return TypedResults.Conflict(new ApiError(result.Message));
            default:
                var view = settings.Read();
                var response = SettingsResponse.From(view);

                return TypedResults.Ok(new SettingsChangeResponse(result.Message, response));
        }
    }
}

/// <summary>
/// The settings of a web app that runs without a runner (the OpenAPI export, tests of other routes): no file to show,
/// and every save is refused.
/// </summary>
public sealed class NoSettings : IRunnerSettings
{
    private const string Message = "This dashboard has no runner, so no repos.yml to edit.";

    public static NoSettings Instance { get; } = new();

    public SettingsView Read() => new(
        File: null,
        Hash: "",
        Version: 0,
        Path: "",
        Error: Message,
        HasComments: false,
        RestartNeeded: [],
        Effective: []);

    public SettingsSaveResult Save(SettingsSave save) => new(SettingsSaveOutcome.Invalid, Message);
}
