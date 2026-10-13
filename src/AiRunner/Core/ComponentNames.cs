namespace AiRunner.Core;

/// <summary>
/// Reads component names that an agent wrote (analysis, SPEC, breakdown). The names must be code components of the
/// project: a typo or a made-up name fails the job instead of sending work to the wrong place.
/// </summary>
public static class ComponentNames
{
    private const string All = "all";
    private const string Unknown = "unknown";

    /// <summary>One name, as written in a table cell: trimmed, no backticks, matched without regard to case.</summary>
    public static string ParseOne(string text, IReadOnlyCollection<string> codeComponents, string where)
    {
        var name = Clean(text);

        if (name.Length == 0)
        {
            throw new FormatException($"{where} has no component; use one of: {Describe(codeComponents)}");
        }

        var known = codeComponents.FirstOrDefault(
            component => string.Equals(component, name, StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {
            throw new FormatException(
                $"{where} names '{name}', which is not a code component; use one of: {Describe(codeComponents)}");
        }

        return known;
    }

    /// <summary>
    /// The "Components affected" value: names separated by commas, or "all" for every code component.
    /// Returns an empty list for "unknown" when <paramref name="mayBeUnknown"/> is true.
    /// </summary>
    public static IReadOnlyList<string> ParseList(
        string text,
        IReadOnlyCollection<string> codeComponents,
        bool mayBeUnknown)
    {
        var parts = text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Clean)
            .Where(part => part.Length > 0)
            .ToList();

        if (parts.Count == 0)
        {
            throw new FormatException($"Components affected is empty; use one of: {Describe(codeComponents)}, or all");
        }

        if (parts.Count == 1 && parts[0].Equals(All, StringComparison.OrdinalIgnoreCase))
        {
            return [.. codeComponents];
        }

        if (parts.Count == 1 && parts[0].Equals(Unknown, StringComparison.OrdinalIgnoreCase))
        {
            if (!mayBeUnknown)
            {
                throw new FormatException(
                    "Components affected is 'unknown', but the analysis is ready; name the components");
            }

            return [];
        }

        var names = parts
            .Select(part => ParseOne(part, codeComponents, "Components affected"))
            .Distinct()
            .ToList();

        return names;
    }

    private static string Clean(string text) => text.Trim().Trim('`', '*', ' ');

    private static string Describe(IReadOnlyCollection<string> codeComponents) =>
        codeComponents.Count == 0 ? "(the project has no code component)" : string.Join(", ", codeComponents);
}
