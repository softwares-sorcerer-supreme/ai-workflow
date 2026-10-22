using AiRunner.Core;

namespace AiRunner.Tests.Core;

public sealed class AgentFrontmatterTests
{
    [Fact]
    public void An_existing_model_line_is_replaced()
    {
        var text = "---\nname: code-scout\nmodel: old\ntools: [read]\n---\nBody\nmodel: stays\n";

        var result = AgentFrontmatter.WithModel(text, "new-model");

        Assert.Equal("---\nname: code-scout\nmodel: new-model\ntools: [read]\n---\nBody\nmodel: stays\n", result);
    }

    [Fact]
    public void A_missing_model_line_is_added_before_the_closing_fence_and_crlf_is_kept()
    {
        var text = "---\r\nname: code-scout\r\n---\r\nBody\r\n";

        var result = AgentFrontmatter.WithModel(text, "m");

        Assert.Equal("---\r\nname: code-scout\r\nmodel: m\r\n---\r\nBody\r\n", result);
    }

    [Fact]
    public void A_text_without_front_matter_is_left_alone()
    {
        Assert.Equal("Body only", AgentFrontmatter.WithModel("Body only", "m"));
        Assert.Equal(
            "---\nname: x\nno closing fence",
            AgentFrontmatter.WithModel("---\nname: x\nno closing fence", "m"));
    }
}
