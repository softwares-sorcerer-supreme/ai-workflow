using AiRunner.Core;

namespace AiRunner.Tests.Core;

public class AgentDriftTests
{
    [Fact]
    public void Same_hash_is_in_sync_a_different_hash_differs_and_a_missing_file_is_missing()
    {
        var source = new Dictionary<string, string>
        {
            ["agents/a.agent.md"] = "h1",
            ["agents/b.agent.md"] = "h2",
            ["skills/s/SKILL.md"] = "h3",
        };
        var installed = new Dictionary<string, string>
        {
            ["agents/a.agent.md"] = "h1",
            ["agents/b.agent.md"] = "other",
        };

        var result = AgentDrift.Compare(source, installed);

        Assert.Equal(AgentStates.InSync, result.States["agents/a.agent.md"]);
        Assert.Equal(AgentStates.Differs, result.States["agents/b.agent.md"]);
        Assert.Equal(AgentStates.Missing, result.States["skills/s/SKILL.md"]);
        Assert.True(result.IsOutOfSync);
    }

    [Fact]
    public void Installed_files_the_repo_does_not_have_are_extra_and_do_not_count_as_drift()
    {
        var source = new Dictionary<string, string> { ["agents/a.agent.md"] = "h1" };
        var installed = new Dictionary<string, string>
        {
            ["agents/a.agent.md"] = "h1",
            ["agents/mine.agent.md"] = "x",
        };

        var result = AgentDrift.Compare(source, installed);

        Assert.Equal(["agents/mine.agent.md"], result.Extra);
        Assert.False(result.IsOutOfSync);
    }

    [Fact]
    public void Nothing_at_all_is_in_sync()
    {
        var result = AgentDrift.Compare(new Dictionary<string, string>(), new Dictionary<string, string>());

        Assert.False(result.IsOutOfSync);
    }
}
