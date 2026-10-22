using AiRunner.Configuration;
using AiRunner.Core;

namespace AiRunner.Tests.Core;

/// <summary>
/// The order of <see cref="ModelResolver"/>: issue override, fix rounds, simple/complex, default, none.
/// </summary>
public sealed class ModelResolverTests
{
    private static readonly ModelSetting Full = new()
    {
        Default = "m-default",
        Simple = "m-simple",
        Complex = "m-complex",
        FixRounds = "m-fix",
    };

    public static TheoryData<ModelSetting?, string?, ModelComplexity, bool, string?, string> Table => new()
    {
        // setting, override, complexity, isFixRound, expected model, expected reason
        { Full, "m-override", ModelComplexity.Complex, true, "m-override", ModelResolver.ReasonOverride },
        { Full, null, ModelComplexity.Complex, true, "m-fix", ModelResolver.ReasonFixRounds },
        { Full, null, ModelComplexity.Complex, false, "m-complex", ModelResolver.ReasonComplex },
        { Full, null, ModelComplexity.Simple, false, "m-simple", ModelResolver.ReasonSimple },
        { Full, null, ModelComplexity.Unknown, false, "m-default", ModelResolver.ReasonDefault },
        {
            new ModelSetting { Default = "m-default" },
            null, ModelComplexity.Complex, false, "m-default", ModelResolver.ReasonDefault
        },
        {
            new ModelSetting { Default = "m-default" },
            null, ModelComplexity.Simple, true, "m-default", ModelResolver.ReasonDefault
        },
        {
            new ModelSetting { Complex = "m-complex" },
            null, ModelComplexity.Simple, false, null, ModelResolver.ReasonNone
        },
        {
            new ModelSetting { FixRounds = "m-fix" },
            null, ModelComplexity.Unknown, false, null, ModelResolver.ReasonNone
        },
        { null, null, ModelComplexity.Unknown, false, null, ModelResolver.ReasonNone },
        { null, "m-override", ModelComplexity.Unknown, false, "m-override", ModelResolver.ReasonOverride },
        { Full, "  ", ModelComplexity.Unknown, false, "m-default", ModelResolver.ReasonDefault },
        { new ModelSetting { Default = " " }, null, ModelComplexity.Unknown, false, null, ModelResolver.ReasonNone },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void First_matching_rule_wins(
        ModelSetting? setting,
        string? issueOverride,
        ModelComplexity complexity,
        bool isFixRound,
        string? expectedModel,
        string expectedReason)
    {
        var choice = ModelResolver.Resolve(new ModelInput(setting, issueOverride, complexity, isFixRound));

        Assert.Equal(expectedModel, choice.Model);
        Assert.Equal(expectedReason, choice.Reason);
    }
}
