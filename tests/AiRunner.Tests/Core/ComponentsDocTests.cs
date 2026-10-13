using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

public class ComponentsDocTests
{
    private const string Root = "/work/mbh";

    private static string Path(params string[] parts) => string.Join('/', [Root, .. parts]).Replace('/', System.IO.Path.DirectorySeparatorChar);

    private static ComponentsDocInput Input(
        ProjectConfig project,
        string targetRepo,
        string? targetComponent = null,
        bool writable = true,
        params string[] existingFiles)
    {
        var existing = existingFiles.Select(file => Path(file.Split('/'))).ToHashSet();

        return new ComponentsDocInput(project, Root.Replace('/', System.IO.Path.DirectorySeparatorChar), targetRepo, targetComponent, writable, existing.Contains);
    }

    [Fact]
    public void Only_the_target_component_may_be_written_in_a_separate_issues_layout()
    {
        var project = TestProjects.SeparateIssuesRepo();
        var input = Input(project, "org/mbh-backend");

        Assert.Equal(ComponentsDoc.ReadOnlyRole, ComponentsDoc.RoleOf(input, project.Components[0]));
        Assert.Equal(ComponentsDoc.TargetRole, ComponentsDoc.RoleOf(input, project.Components[1]));
        Assert.Equal(ComponentsDoc.ReadOnlyRole, ComponentsDoc.RoleOf(input, project.Components[2]));
    }

    [Fact]
    public void In_a_monorepo_the_named_component_is_the_only_target()
    {
        var project = TestProjects.Monorepo();

        var named = Input(project, "org/mono", targetComponent: "api");
        var unnamed = Input(project, "org/mono");

        Assert.Equal(ComponentsDoc.ReadOnlyRole, ComponentsDoc.RoleOf(named, project.Components[0]));
        Assert.Equal(ComponentsDoc.TargetRole, ComponentsDoc.RoleOf(named, project.Components[1]));
        Assert.Equal(ComponentsDoc.TargetRole, ComponentsDoc.RoleOf(unnamed, project.Components[0]));
        Assert.Equal(ComponentsDoc.TargetRole, ComponentsDoc.RoleOf(unnamed, project.Components[1]));
    }

    [Fact]
    public void Nothing_is_a_target_when_the_job_may_not_write()
    {
        var project = TestProjects.SeparateIssuesRepo();
        var input = Input(project, "org/mbh-issues", writable: false);

        Assert.All(project.Components, component =>
            Assert.Equal(ComponentsDoc.ReadOnlyRole, ComponentsDoc.RoleOf(input, component)));
    }

    [Fact]
    public void A_component_folder_is_its_clone_plus_its_path()
    {
        var project = TestProjects.Monorepo();

        Assert.Equal(Path("mono", "apps", "web"), ComponentsDoc.FolderOf(Input(project, "org/mono").ProjectRoot, project.Components[0]));
        Assert.Equal(Path("mono"), ComponentsDoc.FolderOf(Input(project, "org/mono").ProjectRoot, new ComponentConfig { Repo = "org/mono" }));
    }

    [Fact]
    public void Agents_file_of_the_component_wins_over_the_one_in_the_repo_root()
    {
        var project = TestProjects.Monorepo();

        var text = ComponentsDoc.Build(Input(
            project, "org/mono", existingFiles: ["mono/AGENTS.md", "mono/apps/web/AGENTS.md"]));

        var web = text.Split('\n').Single(line => line.StartsWith("| web "));
        var api = text.Split('\n').Single(line => line.StartsWith("| api "));
        Assert.Contains(Path("mono", "apps", "web", "AGENTS.md"), web);
        Assert.Contains(Path("mono", "AGENTS.md"), api);
    }

    [Fact]
    public void A_component_without_any_agents_file_says_none()
    {
        var project = TestProjects.Single();

        var text = ComponentsDoc.Build(Input(project, TestProjects.Repo));

        Assert.Contains("| none |", text);
    }

    [Fact]
    public void The_project_description_is_named_when_it_exists_and_missing_is_no_error()
    {
        var project = TestProjects.SeparateIssuesRepo();

        var present = ComponentsDoc.Build(Input(project, "org/mbh-backend", existingFiles: ["mbh-issues/PROJECT.md"]));
        var missing = ComponentsDoc.Build(Input(project, "org/mbh-backend"));

        Assert.Contains("Project description (read it first): " + Path("mbh-issues", "PROJECT.md"), present);
        Assert.Contains("Project description: none", missing);
        Assert.Contains("not an error", missing);
    }

    [Fact]
    public void The_text_is_ascii_and_marks_issues_and_code()
    {
        var project = TestProjects.SeparateIssuesRepo();

        var text = ComponentsDoc.Build(Input(project, "org/mbh-backend"));

        Assert.All(text, character => Assert.True(character < 128, $"non-ASCII character {character}"));
        var issues = text.Split('\n').Single(line => line.StartsWith("| issues "));
        Assert.Contains("| no | yes |", issues);   // code: no, issues: yes
    }
}
