using System.Text.Json;

namespace AiRunner.Core;

/// <summary>
/// What Copilot billed for one or more runs, read from the JSON of <c>copilot --usage-output-file</c>. Copilot bills
/// AI credits by tokens, so the four token kinds are kept apart: each has its own price per model.
/// </summary>
/// <param name="NanoAiu">The CLI's <c>totalNanoAiu</c>, kept as it is (no unit conversion).</param>
/// <param name="InputTokens">Input tokens that were neither read from nor written to the cache.</param>
public sealed record CopilotUsage(
    long NanoAiu,
    long InputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens)
{
    public static readonly CopilotUsage Zero = new(0, 0, 0, 0, 0);

    /// <summary>
    /// The column names of the five values in cost_log and cost.csv, in the order of <see cref="Values"/>: the one
    /// place that lists them, so a new token kind is added here and in the record only.
    /// </summary>
    public static readonly IReadOnlyList<string> ColumnNames =
        ["nano_aiu", "input_tokens", "cache_read_tokens", "cache_write_tokens", "output_tokens"];

    /// <summary>The five values in the order of <see cref="ColumnNames"/>.</summary>
    public IReadOnlyList<long> Values() => [NanoAiu, InputTokens, CacheReadTokens, CacheWriteTokens, OutputTokens];

    /// <summary>The usage from values in the order of <see cref="ColumnNames"/> (a row read back).</summary>
    public static CopilotUsage FromValues(IReadOnlyList<long> values) =>
        new(values[0], values[1], values[2], values[3], values[4]);

    public CopilotUsage Add(CopilotUsage other) =>
        new(
            NanoAiu + other.NanoAiu,
            InputTokens + other.InputTokens,
            CacheReadTokens + other.CacheReadTokens,
            CacheWriteTokens + other.CacheWriteTokens,
            OutputTokens + other.OutputTokens);

    /// <summary>The sum of several usages; <see cref="Zero"/> for none.</summary>
    public static CopilotUsage Sum(IEnumerable<CopilotUsage> usages) =>
        usages.Aggregate(Zero, (sum, next) => sum.Add(next));

    /// <summary>
    /// One usage file: its totals and what each agent spent on each model (<c>agentMetrics</c>; a file without it
    /// gives its <c>modelMetrics</c> as agent "main"). Null when the file is not JSON or has neither cost nor tokens.
    /// </summary>
    /// <param name="run">The Copilot run the file belongs to ("copilot", "round-2-copilot").</param>
    public static JobUsage? Parse(string json, string run)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var total = root.ValueKind == JsonValueKind.Object ? FromNode(root) : null;
            if (total is null)
            {
                return null;
            }

            var parts = new List<UsagePart>();
            if (root.TryGetProperty("agentMetrics", out var agents) && agents.ValueKind == JsonValueKind.Object)
            {
                foreach (var agent in agents.EnumerateObject())
                {
                    var agentModels = agent.Value.ValueKind == JsonValueKind.Object &&
                                      agent.Value.TryGetProperty("modelMetrics", out var agentModelMetrics)
                        ? agentModelMetrics
                        : default;
                    var agentParts = PartsOf(agentModels, run, agent.Name);
                    parts.AddRange(agentParts);
                }
            }
            else if (root.TryGetProperty("modelMetrics", out var modelMetrics))
            {
                var mainParts = PartsOf(modelMetrics, run, "main");
                parts.AddRange(mainParts);
            }

            return new JobUsage(total, parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<UsagePart> PartsOf(JsonElement models, string run, string agent)
    {
        if (models.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var model in models.EnumerateObject())
        {
            if (model.Value.ValueKind == JsonValueKind.Object && FromNode(model.Value) is { } usage)
            {
                yield return new UsagePart(run, agent, model.Name, usage);
            }
        }
    }

    /// <summary>
    /// The usage of a node with <c>totalNanoAiu</c> and/or <c>tokenDetails</c>, or null when it has neither.
    /// </summary>
    private static CopilotUsage? FromNode(JsonElement node)
    {
        var hasCost = node.TryGetProperty("totalNanoAiu", out var costElement);
        var hasTokens = node.TryGetProperty("tokenDetails", out var tokens);
        if (!hasCost && !hasTokens)
        {
            return null;
        }

        var nanoAiu = hasCost ? Number(costElement) : 0;

        return new CopilotUsage(
            nanoAiu,
            TokenCount(tokens, "input"),
            TokenCount(tokens, "cache_read"),
            TokenCount(tokens, "cache_write"),
            TokenCount(tokens, "output"));
    }

    private static long TokenCount(JsonElement tokens, string kind)
    {
        if (tokens.ValueKind != JsonValueKind.Object || !tokens.TryGetProperty(kind, out var entry))
        {
            return 0;
        }

        return entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("tokenCount", out var count)
            ? Number(count)
            : 0;
    }

    private static long Number(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value) ? value : 0;
}

/// <summary>What one agent spent on one model in one Copilot run of a job.</summary>
/// <param name="Run">The run ("copilot" for a one-call job, "round-2-copilot" for a fix round).</param>
/// <param name="Agent">"main", or a subagent such as "code-reviewer".</param>
public sealed record UsagePart(string Run, string Agent, string Model, CopilotUsage Usage);

/// <summary>What Copilot billed for a job: the sum of its runs, and the per run, agent and model parts.</summary>
public sealed record JobUsage(CopilotUsage Total, IReadOnlyList<UsagePart> Parts)
{
    /// <summary>The usages of several runs as one; the parts keep which run each came from.</summary>
    public static JobUsage Combine(IReadOnlyList<JobUsage> usages)
    {
        var total = CopilotUsage.Sum(usages.Select(usage => usage.Total));
        var parts = usages
            .SelectMany(usage => usage.Parts)
            .ToList();

        return new JobUsage(total, parts);
    }
}
