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

    /// <summary>The usage from values in the order of <see cref="ColumnNames"/> (a row read back from storage).</summary>
    public static CopilotUsage FromValues(IReadOnlyList<long> values) =>
        new(values[0], values[1], values[2], values[3], values[4]);

    public CopilotUsage Add(CopilotUsage other) =>
        new(
            NanoAiu + other.NanoAiu,
            InputTokens + other.InputTokens,
            CacheReadTokens + other.CacheReadTokens,
            CacheWriteTokens + other.CacheWriteTokens,
            OutputTokens + other.OutputTokens);

    /// <summary>The totals of one usage file, or null when it is not JSON or has neither cost nor tokens.</summary>
    public static CopilotUsage? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var hasCost = root.TryGetProperty("totalNanoAiu", out var costElement);
            var hasTokens = root.TryGetProperty("tokenDetails", out var tokens);
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
        catch (JsonException)
        {
            return null;
        }
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
