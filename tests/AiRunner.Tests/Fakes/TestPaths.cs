namespace AiRunner.Tests.Fakes;

public static class TestPaths
{
    /// <summary>The repo folder: the nearest parent of the test binaries that holds repos.yml.</summary>
    public static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "repos.yml")))
        {
            folder = folder.Parent;
        }

        Assert.NotNull(folder);

        return folder.FullName;
    }
}
