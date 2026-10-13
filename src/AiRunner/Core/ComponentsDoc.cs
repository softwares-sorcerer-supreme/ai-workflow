using System.Text;
using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>What <see cref="ComponentsDoc.Build"/> writes components.md from.</summary>
/// <param name="ProjectRoot">The project folder; every repo is cloned in a folder below it.</param>
/// <param name="TargetRepo">The repo the job works on.</param>
/// <param name="TargetComponent">The one component the job may change, or null for every code component in the target repo.</param>
/// <param name="Writable">False for analysis and review: nothing is a target then.</param>
/// <param name="FileExists">Tells whether a file is on disk (full path); keeps this class free of I/O.</param>
public sealed record ComponentsDocInput(
    ProjectConfig Project,
    string ProjectRoot,
    string TargetRepo,
    string? TargetComponent,
    bool Writable,
    Func<string, bool> FileExists);

/// <summary>
/// components.md: the map the agent reads first. One row per component with where it is on disk, which AGENTS.md
/// belongs to it and whether the job may write there. The agent sees all components, but a job changes only one.
/// </summary>
public static class ComponentsDoc
{
    public const string FileName = "components.md";
    public const string TargetRole = "target - you may write here";
    public const string ReadOnlyRole = "read-only";

    public static string Build(ComponentsDocInput input)
    {
        var project = input.Project;
        var builder = new StringBuilder();

        builder.AppendLine("# Components").AppendLine();
        builder.AppendLine($"Project: {project.Name}").AppendLine();
        builder.AppendLine(ProjectDescriptionLine(input)).AppendLine();
        builder.AppendLine("| Component | Repo | Path | Folder | Code | Issues | AGENTS.md | Role |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var component in project.Components)
        {
            var folder = FolderOf(input.ProjectRoot, component);
            var agentsFile = AgentsFileOf(input, component);
            var isIssues = component.Name == project.IssuesIn;
            builder.AppendLine(
                $"| {component.Name} | {component.Repo} | {component.Path} | {folder} | {YesNo(component.Code)} | " +
                $"{YesNo(isIssues)} | {agentsFile ?? "none"} | {RoleOf(input, component)} |");
        }

        builder.AppendLine();
        builder.AppendLine("Read the AGENTS.md of a component before you read or change its code.");
        builder.AppendLine($"Write only where the Role says `{TargetRole}`. Everything else is `{ReadOnlyRole}`.");

        return builder.ToString();
    }

    /// <summary>The role of a component in this job.</summary>
    public static string RoleOf(ComponentsDocInput input, ComponentConfig component)
    {
        var isTarget = input.Writable &&
                       component.Code &&
                       string.Equals(component.Repo, input.TargetRepo, StringComparison.OrdinalIgnoreCase) &&
                       (input.TargetComponent is null || input.TargetComponent == component.Name);

        return isTarget ? TargetRole : ReadOnlyRole;
    }

    /// <summary>The component folder: the clone of its repo, plus its path.</summary>
    public static string FolderOf(string projectRoot, ComponentConfig component)
    {
        var cloneFolder = Path.Combine(projectRoot, ProjectConfig.FolderOf(component.Repo));

        return component.FolderIn(cloneFolder);
    }

    /// <summary>The PROJECT.md of the project: in the folder of the issues component.</summary>
    public static string ProjectDescriptionFile(string projectRoot, ProjectConfig project) =>
        Path.Combine(FolderOf(projectRoot, project.IssuesComponent), project.Context);

    /// <summary>The AGENTS.md of the component folder, else the one in the root of its repo, else null.</summary>
    private static string? AgentsFileOf(ComponentsDocInput input, ComponentConfig component)
    {
        var componentFile = Path.Combine(FolderOf(input.ProjectRoot, component), "AGENTS.md");
        if (input.FileExists(componentFile))
        {
            return componentFile;
        }

        var repoFile = Path.Combine(input.ProjectRoot, ProjectConfig.FolderOf(component.Repo), "AGENTS.md");
        return input.FileExists(repoFile) ? repoFile : null;
    }

    private static string ProjectDescriptionLine(ComponentsDocInput input)
    {
        var file = ProjectDescriptionFile(input.ProjectRoot, input.Project);

        return input.FileExists(file)
            ? $"Project description (read it first): {file}"
            : $"Project description: none ({input.Project.Context} is missing in the issues component; this is not an error).";
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
