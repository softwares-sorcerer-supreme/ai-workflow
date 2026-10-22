using AiRunner.Core;

namespace AiRunner.Tests.Core;

public class AgentPathsTests
{
    [Theory]
    [InlineData("agents/code-scout.agent.md", AgentKinds.Agent)]
    [InlineData("skills/grill-me/SKILL.md", AgentKinds.Skill)]
    [InlineData("skills/grill-me/sub/notes.md", AgentKinds.Skill)]
    [InlineData("templates/spec.md", AgentKinds.Template)]
    public void Known_shapes_have_a_kind(string path, string kind)
    {
        Assert.Equal(kind, AgentPaths.KindOf(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("agents")]
    [InlineData("agents/")]
    [InlineData("agents/.agent.md")]
    [InlineData("agents/readme.md")]
    [InlineData("agents/sub/x.agent.md")]
    [InlineData("skills/x.md")]
    [InlineData("../agents/x.agent.md")]
    [InlineData("agents/../repos.yml")]
    [InlineData("skills/../../repos.yml")]
    [InlineData("/agents/x.agent.md")]
    [InlineData("agents\\x.agent.md")]
    [InlineData("C:/agents/x.agent.md")]
    [InlineData("agents//x.agent.md")]
    [InlineData("src/Program.cs")]
    [InlineData("repos.yml")]
    public void Anything_else_is_refused(string path)
    {
        Assert.Null(AgentPaths.KindOf(path));
    }

    [Fact]
    public void Templates_are_not_editable_agents_and_skills_are()
    {
        Assert.False(AgentPaths.IsEditable("templates/spec.md"));
        Assert.True(AgentPaths.IsEditable("agents/a.agent.md"));
        Assert.True(AgentPaths.IsEditable("skills/s/SKILL.md"));
    }
}
