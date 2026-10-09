namespace AiRunner.Tests.Core;

public class MarkersTests
{
    [Fact]
    public void Extract_uses_the_last_start_marker()
    {
        var text = "template: <!-- X_START -->\n{fill}\n<!-- X_END -->\nanswer: <!-- X_START -->\nreal\n<!-- X_END -->";

        Assert.Equal("real", Markers.Extract(text, "X"));
    }

    [Fact]
    public void Extract_returns_null_without_end_marker() =>
        Assert.Null(Markers.Extract("<!-- X_START -->\nhalf", "X"));

    [Fact]
    public void Wrap_and_extract_round_trip()
    {
        var wrapped = Markers.Wrap("body", Markers.Review);

        Assert.True(Markers.Contains(wrapped, Markers.Review));
        Assert.Equal("body", Markers.Extract(wrapped, Markers.Review));
    }
}

public class AnalysisParserTests
{
    [Fact]
    public void Questions_open_adds_label_and_removes_ready_and_complexity()
    {
        var block = Samples.Analysis("questions-open", "simple");
        var result = AnalysisParser.Parse(block);

        Assert.Equal(AnalysisStatus.QuestionsOpen, result.Status);
        Assert.Equal([Labels.QuestionsOpen], result.LabelsToAdd);
        Assert.Contains(Labels.Ready, result.LabelsToRemove);
        Assert.Contains(Labels.Simple, result.LabelsToRemove);
    }

    [Theory]
    [InlineData("simple", Labels.Simple, Labels.Complex)]
    [InlineData("complex", Labels.Complex, Labels.Simple)]
    public void Ready_sets_ready_and_complexity(string complexity, string add, string remove)
    {
        var block = Samples.Analysis("ready", complexity);
        var result = AnalysisParser.Parse(block);

        Assert.Equal([Labels.Ready, add], result.LabelsToAdd);
        Assert.Equal([Labels.QuestionsOpen, remove], result.LabelsToRemove);
    }

    [Theory]
    [InlineData("questions-open | ready", "simple")]   // agent copied the template
    [InlineData("done", "simple")]
    [InlineData("ready", "medium")]
    public void Values_outside_the_vocabulary_fail(string status, string complexity) =>
        Assert.Throws<FormatException>(() => AnalysisParser.Parse(Samples.Analysis(status, complexity)));

    [Fact]
    public void Missing_status_line_fails() =>
        Assert.Throws<FormatException>(() => AnalysisParser.Parse("**Complexity:** simple"));

    [Fact]
    public void Plain_field_without_bold_is_read() =>
        Assert.Equal("ready", MarkdownSections.ReadField("Status: Ready\n", "Status"));
}

public class BreakdownParserTests
{
    [Fact]
    public void Reads_rows_dependencies_developer_tag_and_contract()
    {
        var block = Samples.Analysis("ready", "complex", Samples.BreakdownTable);
        var breakdown = BreakdownParser.Parse(block);

        Assert.Equal(3, breakdown.Tasks.Count);
        Assert.Equal("Export entity", breakdown.Tasks[0].Title);
        Assert.Equal("src/Domain/Export.cs", breakdown.Tasks[0].Touches);
        Assert.Empty(breakdown.Tasks[0].DependsOn);
        Assert.Equal([1, 2], breakdown.Tasks[2].DependsOn);
        Assert.True(breakdown.Tasks[2].NeedsDeveloper);
        Assert.Equal("Export API", breakdown.Tasks[2].Title);
        Assert.StartsWith("entity Export(Id", breakdown.SharedContract);
    }

    [Fact]
    public void None_breakdown_fails_clearly()
    {
        var exception = Assert.Throws<FormatException>(
            () => BreakdownParser.Parse(Samples.Analysis("ready", "complex")));

        Assert.Contains("None", exception.Message);
    }

    [Fact]
    public void Wrong_numbering_fails() =>
        Assert.Throws<FormatException>(() => BreakdownParser.Parse(Samples.Analysis(
            "ready",
            "complex",
            Samples.BreakdownTable.Replace("| 2 | Export command", "| 5 | Export command"))));

    [Fact]
    public void Missing_shared_contract_fails() =>
        Assert.Throws<FormatException>(() => BreakdownParser.Parse(Samples.Analysis(
            "ready",
            "complex",
            Samples.BreakdownTable.Replace("Shared contract:", "Contract -"))));

    [Fact]
    public void Wrong_column_count_fails() =>
        Assert.Throws<FormatException>(() => BreakdownParser.Parse(Samples.Analysis(
            "ready",
            "complex",
            "| # | Sub-task | Touches | Depends on |\n| --- | --- | --- | --- |\n| 1 | A - scope | x | - |\nShared contract: X")));
}

public class ReviewParserTests
{
    [Fact]
    public void Counts_findings_by_severity()
    {
        var block = Samples.Review();
        var result = ReviewParser.Parse(block);

        Assert.Equal("needs changes", result.Verdict);
        Assert.Equal((1, 0, 1), (result.Bugs, result.Risks, result.Style));
    }

    [Fact]
    public void Unknown_verdict_fails() =>
        Assert.Throws<FormatException>(() => ReviewParser.Parse(Samples.Review(verdict: "ok")));

    [Fact]
    public void Unknown_severity_fails() =>
        Assert.Throws<FormatException>(() => ReviewParser.Parse(Samples.Review(severity: "critical")));

    [Fact]
    public void Findings_none_is_fine()
    {
        var text = Samples.Review(verdict: "looks good");
        var start = text.IndexOf("| #", StringComparison.Ordinal);
        var end = text.IndexOf("### Spec check", StringComparison.Ordinal);

        var result = ReviewParser.Parse(text[..start] + "None\n\n" + text[end..]);

        Assert.Equal((0, 0, 0), (result.Bugs, result.Risks, result.Style));
    }
}

public class SubIssueBodyTests
{
    private static readonly SubTask Task2 = new(2, "Export command", "Add the command.", "src/App", [1], false);

    [Fact]
    public void Build_and_parse_round_trip()
    {
        var draft = new SubIssueDraft(12, Task2, 3, "entity Export");
        var body = SubIssueBody.Build(draft);
        var info = SubIssueBody.Parse(body);

        Assert.Equal(new SubIssueInfo(12, 2, 3, "feat/issue-12"), info);
        Assert.Contains("Depends on: #12.1", body);
        Assert.Contains("entity Export", body);
        Assert.Equal("#12.2 Export command", SubIssueBody.Title(12, Task2));
    }

    [Fact]
    public void Normal_issue_body_is_not_a_sub_issue() => Assert.Null(SubIssueBody.Parse("Please add CSV export."));

    [Fact]
    public void List_marker_is_found_in_the_newest_bot_comment()
    {
        var oldMarker = SubIssueBody.ListMarker([5, 6]);
        var newestMarker = SubIssueBody.ListMarker([7, 8, 9]);
        var humanMarker = SubIssueBody.ListMarker([1]);
        var comments = new[]
        {
            Samples.Bot(1, oldMarker),
            Samples.Bot(2, newestMarker),
            Samples.Human(3, humanMarker),
        };

        Assert.Equal([7, 8, 9], SubIssueBody.FindList(comments));
    }

    [Fact]
    public void Done_when_closed_or_pr_merged_into_feature_branch()
    {
        var open = Samples.Issue(20);

        GbPull CreatePull(string head, string @base, bool merged) =>
            new(
                1,
                "t",
                new GbRef(head, "s"),
                new GbRef(@base, "b"),
                DateTimeOffset.UtcNow,
                Merged: merged,
                State: "closed");

        var slot = new SubIssueSlot(12, 1);
        var closed = open with
        {
            State = "closed"
        };
        var mergedPull = CreatePull("ai/issue-12-1", "feat/issue-12", true);
        var unmergedPull = CreatePull("ai/issue-12-1", "feat/issue-12", false);
        var wrongBasePull = CreatePull("ai/issue-12-1", "main", true);

        Assert.False(SubIssueBody.IsDone(open, slot, []));
        Assert.True(SubIssueBody.IsDone(closed, slot, []));
        Assert.True(SubIssueBody.IsDone(open, slot, [mergedPull]));
        Assert.False(SubIssueBody.IsDone(open, slot, [unmergedPull]));
        Assert.False(SubIssueBody.IsDone(open, slot, [wrongBasePull]));
    }
}

public class PathGlobTests
{
    [Theory]
    [InlineData("README.md", "**/*.md", true)]
    [InlineData("docs/a/b.png", "docs/**", true)]
    [InlineData("src/docs.cs", "docs/**", false)]
    [InlineData("src/a.generated.cs", "**/*.generated.cs", true)]
    [InlineData("src/sub/x.cs", "src/*.cs", false)]
    public void Matches(string path, string glob, bool expected) =>
        Assert.Equal(expected, PathGlob.IsMatch(path, glob));

    [Fact]
    public void AllMatch_needs_every_file_and_at_least_one_glob()
    {
        Assert.True(PathGlob.AllMatch(["a.md", "docs/x.png"], ["**/*.md", "docs/**"]));
        Assert.False(PathGlob.AllMatch(["a.md", "src/x.cs"], ["**/*.md"]));
        Assert.False(PathGlob.AllMatch(["a.md"], []));
    }
}

public class AsciiSanitizerTests
{
    [Fact]
    public void Keeps_text_readable_without_non_ascii()
    {
        var clean = AsciiSanitizer.Clean("Tiếng Việt – “đúng” → ok 🤖");

        Assert.Equal("Tieng Viet - \"dung\" -> ok ", clean);
        Assert.True(clean.All(character => character < 128));
    }
}
