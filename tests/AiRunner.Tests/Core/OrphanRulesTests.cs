using AiRunner.Core;

namespace AiRunner.Tests.Core;

public sealed class OrphanRulesTests
{
    private static readonly string[] Projects = ["mbh", "other-project"];

    [Theory]
    [InlineData("old-project")]
    [InlineData("a b")]
    [InlineData("project.v2")]
    public void A_plain_folder_name_of_no_project_is_allowed(string folder)
    {
        Assert.Null(OrphanRules.WhyRefused(folder, Projects));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/etc")]
    [InlineData("\\windows")]
    [InlineData("C:\\temp")]
    [InlineData("C:")]
    [InlineData("\\\\server\\share")]
    [InlineData(".no-hooks")]
    [InlineData(".hidden")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("a\0b")]
    public void A_name_that_is_not_plain_is_refused(string? folder)
    {
        Assert.NotNull(OrphanRules.WhyRefused(folder, Projects));
    }

    [Theory]
    [InlineData("mbh")]
    [InlineData("MBH")]
    [InlineData("Other-Project")]
    public void A_project_folder_is_refused_in_any_case(string folder)
    {
        var reason = OrphanRules.WhyRefused(folder, Projects);

        Assert.NotNull(reason);
        Assert.Contains("project", reason);
    }
}
