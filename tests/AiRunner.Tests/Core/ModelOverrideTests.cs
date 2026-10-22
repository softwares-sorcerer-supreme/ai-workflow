using AiRunner.Core;

namespace AiRunner.Tests.Core;

/// <summary>The AI_SETTINGS tag: bot comments only, the newest wins, empty removes, a sub-issue falls back to its parent.</summary>
public sealed class ModelOverrideTests
{
    private static HostComment BotTag(long id, string workflow, string model) =>
        Samples.Bot(id, Markers.SettingTag(ModelOverride.KeyOf(workflow), model));

    [Fact]
    public void The_newest_bot_tag_of_the_workflow_wins()
    {
        var comments = new[]
        {
            BotTag(1, "implement", "m-old"),
            BotTag(5, "implement", "m-new"),
            BotTag(3, "review", "m-r"),
        };

        Assert.Equal("m-new", ModelOverride.For("implement", comments, null));
        Assert.Equal("m-r", ModelOverride.For("review", comments, null));
        Assert.Null(ModelOverride.For("analyze", comments, null));
    }

    [Fact]
    public void A_human_cannot_fake_the_tag()
    {
        var humanTag = Samples.Human(2, Markers.SettingTag("models.implement", "m-human"));

        Assert.Equal("m-bot", ModelOverride.For("implement", [BotTag(1, "implement", "m-bot"), humanTag], null));
        Assert.Null(ModelOverride.For("implement", [humanTag], null));
    }

    [Fact]
    public void An_empty_value_removes_the_override_and_stops_the_search()
    {
        var own = new[] { BotTag(1, "implement", "m-own"), BotTag(2, "implement", "") };
        var parent = new[] { BotTag(1, "implement", "m-parent") };

        Assert.Null(ModelOverride.For("implement", own, parent));
    }

    [Fact]
    public void A_sub_issue_reads_its_own_tag_then_its_parents()
    {
        var own = new[] { BotTag(1, "implement", "m-own") };
        var parent = new[] { BotTag(1, "implement", "m-parent") };

        Assert.Equal("m-own", ModelOverride.For("implement", own, parent));
        Assert.Equal("m-parent", ModelOverride.For("implement", [], parent));
        Assert.Null(ModelOverride.For("implement", [], [Samples.Bot(1, "nothing here")]));
    }

    [Theory]
    [InlineData("claude-opus-5.5", true)]
    [InlineData("gpt-5.1-codex:high", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("x-->", false)]
    [InlineData("a--b", false)]
    public void Only_names_that_are_safe_in_a_tag_are_valid(string model, bool isValid) =>
        Assert.Equal(isValid, ModelOverride.IsValidName(model));
}
