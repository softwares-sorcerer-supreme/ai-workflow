using AiRunner.Core;

namespace AiRunner.Tests.Core;

public sealed class WorkspaceChecksTests
{
    private static readonly string[] Installed = ["analyze-issue.agent.md", "implement-issue.agent.md"];

    private static CloneFacts Facts(
        bool exists = true,
        string[]? changed = null,
        string[]? agents = null,
        string[]? missing = null) =>
        new(exists, changed ?? [], agents ?? [], missing ?? []);

    [Fact]
    public void A_clean_clone_has_no_warnings()
    {
        Assert.Empty(WorkspaceChecks.For(Facts(), Installed));
    }

    [Fact]
    public void A_clone_that_is_not_on_disk_has_no_warnings()
    {
        var facts = Facts(exists: false, changed: ["a.txt"], missing: ["backend: AGENTS.md"]);

        Assert.Empty(WorkspaceChecks.For(facts, Installed));
    }

    [Theory]
    [InlineData("implement-issue.agent.md")]
    [InlineData("IMPLEMENT-ISSUE.agent.md")]
    public void A_repo_agent_with_the_name_of_a_shared_one_is_an_override(string file)
    {
        var warnings = WorkspaceChecks.For(Facts(agents: [file]), Installed);

        var warning = Assert.Single(warnings);
        Assert.Equal(WorkspaceChecks.AgentOverride, warning.Code);
        Assert.Contains(file, warning.Message);
    }

    [Fact]
    public void A_repo_agent_the_runner_does_not_install_is_no_override()
    {
        Assert.Empty(WorkspaceChecks.For(Facts(agents: ["own-helper.agent.md"]), Installed));
    }

    [Fact]
    public void Every_missing_instruction_file_is_a_warning()
    {
        var warnings = WorkspaceChecks.For(Facts(missing: ["backend: AGENTS.md", "web: docs/RULES.md"]), Installed);

        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.Equal(WorkspaceChecks.InstructionsMissing, warning.Code));
        Assert.Contains("docs/RULES.md", warnings[1].Message);
    }

    [Fact]
    public void Changes_between_jobs_are_one_warning_that_lists_a_few_paths()
    {
        string[] changed = ["a", "b", "c", "d", "e", "f", "g"];

        var warning = Assert.Single(WorkspaceChecks.For(Facts(changed: changed), Installed));

        Assert.Equal(WorkspaceChecks.HasChanges, warning.Code);
        Assert.Contains("7 changed", warning.Message);
        Assert.Contains("a, b, c, d, e", warning.Message);
        Assert.Contains("and 2 more", warning.Message);
        Assert.DoesNotContain("f,", warning.Message);
    }
}
