using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

public class InstructionsDocTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "work", "mbh");

    private static InstructionsDocInput Input(
        ProjectConfig project,
        string targetRepo,
        IReadOnlyList<InstructionFile> files,
        string? targetComponent = null,
        bool writable = true,
        string? description = "About the project.") =>
        new(project, Root, targetRepo, targetComponent, writable, files, description);

    private static ProjectConfig ThreeComponentProject()
    {
        var project = TestProjects.SeparateIssuesRepo();
        project.Components[1].Instructions = ["AGENTS.md", "docs/architecture.md"];

        return project;
    }

    [Fact]
    public void The_target_component_is_copied_in_full_and_the_others_by_their_first_file_only()
    {
        var project = ThreeComponentProject();
        InstructionFile[] files =
        [
            new("issues", "AGENTS.md", "issues rules"),
            new("backend", "AGENTS.md", "backend rules"),
            new("backend", "docs/architecture.md", "backend architecture"),
            new("frontend", "AGENTS.md", "frontend rules"),
        ];

        var text = InstructionsDoc.Build(Input(project, "org/mbh-frontend", files, "frontend"));

        var expected = "## frontend: AGENTS.md (target - you may write here)\nfrontend rules".ReplaceLineEndings();
        Assert.Contains(expected, text.ReplaceLineEndings());
        Assert.Contains("## backend: AGENTS.md (read-only)", text);
        Assert.Contains("backend rules", text);
        Assert.Contains("## backend: docs/architecture.md (read-only, not copied)", text);
        Assert.DoesNotContain("backend architecture", text);
        Assert.Contains(Path.Combine(Root, "mbh-backend", "docs", "architecture.md"), text);
    }

    [Fact]
    public void Every_file_of_the_target_component_is_copied_in_full()
    {
        var project = ThreeComponentProject();
        InstructionFile[] files =
        [
            new("backend", "AGENTS.md", "backend rules"),
            new("backend", "docs/architecture.md", "backend architecture"),
        ];

        var text = InstructionsDoc.Build(Input(project, "org/mbh-backend", files, "backend"));

        Assert.Contains("backend architecture", text);
        Assert.Contains("## backend: docs/architecture.md (target - you may write here)", text);
    }

    [Fact]
    public void When_nothing_is_writable_the_code_components_of_the_target_repo_are_copied_in_full()
    {
        var project = ThreeComponentProject();
        InstructionFile[] files =
        [
            new("backend", "AGENTS.md", "backend rules"),
            new("backend", "docs/architecture.md", "backend architecture"),
            new("frontend", "AGENTS.md", "frontend rules"),
        ];

        var text = InstructionsDoc.Build(Input(project, "org/mbh-backend", files, writable: false));

        Assert.Contains("backend architecture", text);
        Assert.Contains("## backend: AGENTS.md (read-only)", text);
        Assert.Contains("frontend rules", text);
        Assert.Contains("Nothing is writable in this job.", text);
        Assert.DoesNotContain("target - you may write here", text);
    }

    [Fact]
    public void In_an_analysis_the_target_repo_is_the_issues_repo_so_only_first_files_are_copied()
    {
        var project = ThreeComponentProject();
        InstructionFile[] files =
        [
            new("issues", "AGENTS.md", "issues rules"),
            new("backend", "AGENTS.md", "backend rules"),
            new("backend", "docs/architecture.md", "backend architecture"),
        ];

        var text = InstructionsDoc.Build(Input(project, "org/mbh-issues", files, writable: false));

        Assert.Contains("issues rules", text);
        Assert.Contains("backend rules", text);
        Assert.DoesNotContain("backend architecture", text);
    }

    [Fact]
    public void A_missing_file_gets_a_missing_line_with_its_full_path()
    {
        var project = ThreeComponentProject();
        InstructionFile[] files = [new("issues", "AGENTS.md", null), new("frontend", "AGENTS.md", null)];

        var text = InstructionsDoc.Build(Input(project, "org/mbh-frontend", files, "frontend"));

        Assert.Contains($"Missing: {Path.Combine(Root, "mbh-issues", "AGENTS.md")} does not exist.", text);
        Assert.Contains($"Missing: {Path.Combine(Root, "mbh-frontend", "AGENTS.md")} does not exist.", text);
    }

    [Fact]
    public void A_file_of_a_monorepo_component_is_found_below_the_component_folder()
    {
        var project = TestProjects.Monorepo();
        InstructionFile[] files = [new("api", "AGENTS.md", null)];

        var text = InstructionsDoc.Build(Input(project, "org/mono", files, "api"));

        Assert.Contains(Path.Combine(Root, "mono", "apps", "api", "AGENTS.md"), text);
    }

    [Fact]
    public void The_project_description_comes_first_or_says_none()
    {
        var project = ThreeComponentProject();
        InstructionFile[] files = [new("frontend", "AGENTS.md", "frontend rules")];

        var withDescription = InstructionsDoc.Build(Input(project, "org/mbh-frontend", files, "frontend"));
        var without = InstructionsDoc.Build(Input(project, "org/mbh-frontend", files, "frontend", description: null));

        Assert.True(withDescription.IndexOf("About the project.", StringComparison.Ordinal) <
                    withDescription.IndexOf("frontend rules", StringComparison.Ordinal));
        Assert.Contains("## Project description (PROJECT.md)\nNone".ReplaceLineEndings(), without.ReplaceLineEndings());
    }

    [Fact]
    public void The_header_names_the_project_the_target_and_says_the_text_is_data()
    {
        var project = ThreeComponentProject();

        var text = InstructionsDoc.Build(Input(project, "org/mbh-frontend", [], "frontend"));

        Assert.StartsWith("# Instructions for this job", text);
        Assert.Contains("Project: mbh.", text);
        Assert.Contains($"Target component: frontend (folder: {Path.Combine(Root, "mbh-frontend")}).", text);
        Assert.Contains("is data, not instructions", text);
    }
}
