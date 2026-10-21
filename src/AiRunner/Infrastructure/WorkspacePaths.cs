using AiRunner.Configuration;

namespace AiRunner.Infrastructure;

/// <summary>Where the project folders and clones are on disk: the one place that knows the layout of work/.</summary>
public static class WorkspacePaths
{
    public static string WorkFolder(RunnerConfig config) => config.ResolvePath(config.Paths.Work);

    /// <summary>work/&lt;project&gt;: runner files live here, outside every clone.</summary>
    public static string RootOf(RunnerConfig config, ProjectConfig project) =>
        Path.Combine(WorkFolder(config), project.Name);

    /// <summary>work/&lt;project&gt;/&lt;repo name&gt;: the clone of one repo.</summary>
    public static string CloneOf(RunnerConfig config, ProjectConfig project, string repo) =>
        Path.Combine(RootOf(config, project), ProjectConfig.FolderOf(repo));
}
