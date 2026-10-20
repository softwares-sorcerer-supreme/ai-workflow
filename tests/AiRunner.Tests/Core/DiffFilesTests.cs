using AiRunner.Core;

namespace AiRunner.Tests.Core;

/// <summary>A git diff cut down to the files of a PR.</summary>
public sealed class DiffFilesTests
{
    private const string Diff = """
        diff --git a/src/a.cs b/src/a.cs
        index 1..2 100644
        --- a/src/a.cs
        +++ b/src/a.cs
        @@ -1 +1 @@
        -old a
        +new a
        diff --git a/src/old.cs b/src/renamed.cs
        similarity index 90%
        rename from src/old.cs
        rename to src/renamed.cs
        diff --git a/docs/merged.md b/docs/merged.md
        +from main
        """;

    [Fact]
    public void Only_the_sections_of_the_given_files_are_kept_by_old_or_new_path()
    {
        var cut = DiffFiles.Only(Diff, ["src/a.cs", "src/renamed.cs"]);

        Assert.Contains("+new a", cut);
        Assert.Contains("rename to src/renamed.cs", cut);
        Assert.DoesNotContain("from main", cut);
    }

    [Fact]
    public void No_matching_file_gives_an_empty_diff() =>
        Assert.Equal("", DiffFiles.Only(Diff, ["other.cs"]));
}
