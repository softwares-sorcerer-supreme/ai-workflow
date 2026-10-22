using AiRunner.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AiRunner.Web;

/// <summary>
/// <c>GET /api/model-options</c>: what the set-model picker offers. <c>Workflows</c> are the workflows that run
/// Copilot; <c>AllowedModels</c> is <c>allowed_models</c> (empty = any model name, the picker shows a text box).
/// </summary>
public sealed record ModelOptionsResponse(
    IReadOnlyList<CodeLabelResponse> Workflows,
    IReadOnlyList<string> AllowedModels);

public static class ModelOptionsApi
{
    public static void MapModelOptionsApi(RouteGroupBuilder api)
    {
        api.MapGet("/model-options", GetOptions)
            .WithTags("Issues")
            .WithName("getModelOptions")
            .WithSummary("The workflows and models the set-model action offers.");
    }

    private static ModelOptionsResponse GetOptions(RunnerConfig config)
    {
        var workflows = Workflows.WithModel
            .Select(workflow => new CodeLabelResponse(workflow, workflow))
            .ToList();

        return new ModelOptionsResponse(workflows, [.. config.AllowedModels]);
    }
}
