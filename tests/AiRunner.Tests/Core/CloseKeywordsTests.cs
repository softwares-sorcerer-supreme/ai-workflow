namespace AiRunner.Tests.Core;

public sealed class CloseKeywordsTests
{
    [Theory]
    [InlineData("Closes #5", "Refs #5")]
    [InlineData("closed #5", "Refs #5")]
    [InlineData("Fixes #5", "Refs #5")]
    [InlineData("FIX #5", "Refs #5")]
    [InlineData("resolves: #5", "Refs: #5")]
    [InlineData("Closes org/issues#5", "Refs org/issues#5")]
    [InlineData("Closes #5, closes #6", "Refs #5, Refs #6")]
    public void Closing_keyword_in_front_of_an_issue_reference_is_replaced(string text, string expected)
    {
        Assert.Equal(expected, CloseKeywords.Neutralize(text));
    }

    [Theory]
    [InlineData("Issue: org/issues#5")]
    [InlineData("See #5 for details")]
    [InlineData("The fixture closes the connection")]
    [InlineData("prefixes #5")]
    [InlineData("`Closes` #5")]
    public void Other_text_is_left_alone(string text)
    {
        Assert.Equal(text, CloseKeywords.Neutralize(text));
    }
}
