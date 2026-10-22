using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>The complexity an issue was analysed as; unknown while there is no ready analysis.</summary>
public enum ModelComplexity
{
    Unknown,
    Simple,
    Complex,
}

/// <summary>What <see cref="ModelResolver.Resolve"/> decides on.</summary>
/// <param name="Setting"><c>models.&lt;workflow&gt;</c> of the project, or null when none is written.</param>
/// <param name="IssueOverride">The model of the issue's override tag (its own, else its parent's), or null.</param>
/// <param name="IsFixRound">A Copilot run after round 1 of implement or fix.</param>
public sealed record ModelInput(ModelSetting? Setting, string? IssueOverride, ModelComplexity Complexity, bool IsFixRound);

/// <summary>The model to pass to Copilot (null = Copilot's default) and why, in words the dashboard shows as they are.</summary>
public sealed record ModelChoice(string? Model, string Reason);

/// <summary>
/// Picks the model of one Copilot run. First match wins: issue override, <c>fix_rounds</c> (fix round), <c>simple</c> or
/// <c>complex</c>, <c>default</c> (the string form), none. Pure, so the order is a table test; the jobs only call it.
/// </summary>
public static class ModelResolver
{
    public const string ReasonOverride = "issue override";
    public const string ReasonFixRounds = "fix rounds";
    public const string ReasonSimple = "simple issue";
    public const string ReasonComplex = "complex issue";
    public const string ReasonDefault = "default";
    public const string ReasonNone = "Copilot default";

    public static ModelChoice Resolve(ModelInput input)
    {
        var setting = input.Setting;

        if (Has(input.IssueOverride))
        {
            return new ModelChoice(input.IssueOverride!.Trim(), ReasonOverride);
        }

        if (input.IsFixRound && Has(setting?.FixRounds))
        {
            return new ModelChoice(setting!.FixRounds!.Trim(), ReasonFixRounds);
        }

        if (input.Complexity == ModelComplexity.Simple && Has(setting?.Simple))
        {
            return new ModelChoice(setting!.Simple!.Trim(), ReasonSimple);
        }

        if (input.Complexity == ModelComplexity.Complex && Has(setting?.Complex))
        {
            return new ModelChoice(setting!.Complex!.Trim(), ReasonComplex);
        }

        if (Has(setting?.Default))
        {
            return new ModelChoice(setting!.Default!.Trim(), ReasonDefault);
        }

        return new ModelChoice(null, ReasonNone);
    }

    private static bool Has(string? model) => !string.IsNullOrWhiteSpace(model);
}
