using AiRunner.Core;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

public class AgentFileRulesTests
{
    private const string GoodAgent =
        "---\nname: my-agent\ndescription: Does a thing.\ntools: [\"read\"]\n---\n\n# Body\n";

    [Fact]
    public void A_valid_agent_has_no_reasons()
    {
        var reasons = AgentFileRules.Check("agents/my-agent.agent.md", GoodAgent);

        Assert.Empty(reasons);
    }

    [Fact]
    public void Crlf_line_endings_are_accepted()
    {
        var reasons = AgentFileRules.Check("agents/my-agent.agent.md", GoodAgent.Replace("\n", "\r\n"));

        Assert.Empty(reasons);
    }

    [Theory]
    [InlineData("no frontmatter at all")]
    [InlineData("---\nname: my-agent\ndescription: x\n")]
    public void Missing_or_unclosed_frontmatter_is_refused(string text)
    {
        var reasons = AgentFileRules.Check("agents/my-agent.agent.md", text);

        Assert.Contains(reasons, reason => reason.Contains("frontmatter"));
    }

    [Fact]
    public void Broken_yaml_is_refused()
    {
        var reasons = AgentFileRules.Check("agents/my-agent.agent.md", "---\nname: [unclosed\n---\n");

        Assert.Contains(reasons, reason => reason.Contains("not valid YAML"));
    }

    [Fact]
    public void A_list_instead_of_a_mapping_is_refused()
    {
        var reasons = AgentFileRules.Check("agents/my-agent.agent.md", "---\n- a\n- b\n---\n");

        Assert.Single(reasons);
    }

    [Fact]
    public void The_name_must_equal_the_file_stem()
    {
        var text = GoodAgent.Replace("name: my-agent", "name: other");

        var reasons = AgentFileRules.Check("agents/my-agent.agent.md", text);

        Assert.Contains(reasons, reason => reason.Contains("name must be \"my-agent\""));
    }

    [Theory]
    [InlineData("description: \"\"")]
    [InlineData("description:")]
    public void An_empty_description_is_refused(string line)
    {
        var text = $"---\nname: my-agent\n{line}\n---\n";

        var reasons = AgentFileRules.Check("agents/my-agent.agent.md", text);

        Assert.Contains(reasons, reason => reason.Contains("description"));
    }

    [Theory]
    [InlineData("tools: read")]
    [InlineData("tools: [[a], b]")]
    [InlineData("tools:\n  a: b")]
    public void Tools_must_be_a_list_of_strings(string line)
    {
        var text = $"---\nname: my-agent\ndescription: x\n{line}\n---\n";

        var reasons = AgentFileRules.Check("agents/my-agent.agent.md", text);

        Assert.Contains(reasons, reason => reason.Contains("tools"));
    }

    [Fact]
    public void A_model_outside_allowed_models_is_refused_only_when_the_list_is_set()
    {
        var text = "---\nname: my-agent\ndescription: x\nmodel: big-model\n---\n";

        var withoutList = AgentFileRules.Check("agents/my-agent.agent.md", text);
        var withEmptyList = AgentFileRules.Check("agents/my-agent.agent.md", text, []);
        var allowed = AgentFileRules.Check("agents/my-agent.agent.md", text, ["big-model"]);
        var refused = AgentFileRules.Check("agents/my-agent.agent.md", text, ["small-model"]);

        Assert.Empty(withoutList);
        Assert.Empty(withEmptyList);
        Assert.Empty(allowed);
        Assert.Single(refused);
    }

    [Fact]
    public void All_reasons_come_back_together()
    {
        var reasons = AgentFileRules.Check("agents/my-agent.agent.md", "---\nname: x\ntools: a\n---\n");

        Assert.Equal(3, reasons.Count);
    }

    [Fact]
    public void A_skill_needs_name_and_description()
    {
        var good = AgentFileRules.Check("skills/s/SKILL.md", "---\nname: s\ndescription: d\n---\n");
        var bad = AgentFileRules.Check("skills/s/SKILL.md", "---\nname: s\n---\n");

        Assert.Empty(good);
        Assert.Single(bad);
    }

    [Fact]
    public void Other_files_are_not_checked()
    {
        var reasons = AgentFileRules.Check("skills/s/notes.md", "no frontmatter");

        Assert.Empty(reasons);
    }

    [Fact]
    public void Every_agent_and_skill_file_of_the_repo_is_accepted()
    {
        var root = TestPaths.RepoRoot();
        var checkedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            var isShared = path.StartsWith("agents/", StringComparison.Ordinal)
                || path.StartsWith("skills/", StringComparison.Ordinal);
            if (!isShared)
            {
                continue;
            }

            var reasons = AgentFileRules.Check(path, File.ReadAllText(file));

            Assert.True(reasons.Count == 0, $"{path}: {string.Join(" ", reasons)}");
            checkedFiles++;
        }

        Assert.True(checkedFiles >= 9, "expected the 5 agents and 4 skills");
    }
}
