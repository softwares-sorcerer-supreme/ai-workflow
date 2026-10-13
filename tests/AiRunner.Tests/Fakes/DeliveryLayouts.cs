using AiRunner.Jobs;

namespace AiRunner.Tests.Fakes;

/// <summary>
/// The three layouts of the spec (multi-repo, separate issues repo, monorepo), each with two code components that have
/// build and test commands, and the small helpers the delivery tests share. The same assertions run on all three.
/// </summary>
public static class DeliveryLayouts
{
    public static TheoryData<string> Names => ["multi-repo", "separate-issues-repo", "monorepo"];

    public static ProjectConfig Create(string name)
    {
        var project = name switch
        {
            "multi-repo" => TestProjects.MultiRepo(),
            "separate-issues-repo" => TestProjects.SeparateIssuesRepo(),
            _ => TestProjects.Monorepo(),
        };

        foreach (var component in project.CodeComponents)
        {
            component.Commands = new CommandsConfig { Build = "make build", Test = "make test" };
        }

        return project;
    }

    /// <summary>The first and the second code component of the project.</summary>
    public static (ComponentConfig First, ComponentConfig Second) Pair(ProjectConfig project)
    {
        var codeComponents = project.CodeComponents.ToList();

        return (codeComponents[0], codeComponents[1]);
    }

    /// <summary>The clone folder of a component's repo in the fake workspace.</summary>
    public static string CloneOf(JobTestKit kit, ComponentConfig component) =>
        Path.Combine(kit.Workspace.Dir, ProjectConfig.FolderOf(component.Repo));

    /// <summary>A file name inside the component folder, from the root of its repo.</summary>
    public static string FileIn(ComponentConfig component, string name) =>
        component.IsWholeRepo ? name : $"{component.Path}/{name}";

    /// <summary>The body of a sub-issue of a parent, built in the given component.</summary>
    public static string SubIssueBodyFor(int parent, int order, string component)
    {
        var task = new SubTask(order, component, $"Task {order}", "Do the work.", "src/x.cs", [], false);

        return SubIssueBody.Build(new SubIssueDraft(parent, task, 3, "contract"));
    }

    /// <summary>The ANALYSIS block comment naming the components, as the analysis job leaves it on an issue.</summary>
    public static void AddAnalysis(JobTestKit kit, int issue, string complexity, string components)
    {
        var analysis = Samples.Analysis("ready", complexity, components: components);
        kit.GitBucket.AddBotComment(issue, Markers.Wrap(analysis, Markers.Analysis));
    }

    /// <summary>A simple issue 12 that is being implemented, with an analysis naming the component.</summary>
    public static void ArrangeSimpleIssue(JobTestKit kit, ComponentConfig component)
    {
        kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implementing);
        AddAnalysis(kit, 12, "simple", component.Name);
    }

    public static ImplementIssueJob ImplementJob(JobTestKit kit) =>
        new(kit.Config, kit.GitBucket, kit.Loader, kit.Workspace, kit.Git, kit.Session, kit.Attachments);

    public static FixReviewJob FixJob(JobTestKit kit) =>
        new(kit.Config, kit.GitBucket, kit.Loader, kit.Workspace, kit.Git, kit.Session, kit.State, kit.Attachments);

    public static ReviewPullRequestJob ReviewJob(JobTestKit kit) =>
        new(kit.Config, kit.GitBucket, kit.Loader, kit.Workspace, kit.Git, kit.Copilot, kit.State, kit.Attachments);
}
