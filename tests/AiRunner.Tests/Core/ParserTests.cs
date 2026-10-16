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
    private static readonly string[] Demo = ["demo"];
    private static readonly string[] BackendAndFrontend = ["backend", "frontend"];

    [Fact]
    public void Questions_open_adds_label_and_removes_ready_and_complexity()
    {
        var block = Samples.Analysis("questions-open", "simple");
        var result = AnalysisParser.Parse(block, Demo);

        Assert.Equal(AnalysisStatus.QuestionsOpen, result.Status);
        Assert.Equal([Labels.QuestionsOpen], result.LabelsToAdd);
        Assert.Contains(Labels.Ready, result.LabelsToRemove);
        Assert.Contains(Labels.Simple, result.LabelsToRemove);
    }

    [Fact]
    public void Grouped_questions_with_empty_groups_do_not_change_what_the_parser_reads()
    {
        var block = Samples.Analysis("questions-open", "complex", components: "demo");
        var questions = MarkdownSections.Get(block, "Questions")!;

        var result = AnalysisParser.Parse(block, Demo);

        Assert.Contains("Behaviour (", questions);
        Assert.Contains("Technical (", questions);
        Assert.Contains("Other (", questions);
        Assert.Contains("- None", questions);
        Assert.Contains("Q1", questions);
        Assert.Equal(AnalysisStatus.QuestionsOpen, result.Status);
        Assert.Equal(Complexity.Complex, result.Complexity);
        Assert.Equal(["demo"], AnalysisParser.ComponentsOf(block, Demo));
    }

    [Fact]
    public void A_flat_question_list_from_an_old_round_still_parses()
    {
        var grouped = Samples.Analysis("questions-open", "simple");
        var flat = grouped.Replace(
            Samples.GroupedQuestions,
            "- Q1: Which columns? (why I ask: 40 columns)\n- Q2: Which format?");

        var result = AnalysisParser.Parse(flat, Demo);

        Assert.Equal(AnalysisStatus.QuestionsOpen, result.Status);
        Assert.Contains("Q2: Which format?", MarkdownSections.Get(flat, "Questions"));
    }

    [Theory]
    [InlineData("simple", Labels.Simple, Labels.Complex)]
    [InlineData("complex", Labels.Complex, Labels.Simple)]
    public void Ready_sets_ready_and_complexity(string complexity, string add, string remove)
    {
        var block = Samples.Analysis("ready", complexity);
        var result = AnalysisParser.Parse(block, Demo);

        Assert.Equal([Labels.Ready, add], result.LabelsToAdd);
        Assert.Equal([Labels.QuestionsOpen, remove], result.LabelsToRemove);
    }

    [Theory]
    [InlineData("questions-open | ready", "simple")]   // agent copied the template
    [InlineData("done", "simple")]
    [InlineData("ready", "medium")]
    public void Values_outside_the_vocabulary_fail(string status, string complexity) =>
        Assert.Throws<FormatException>(() => AnalysisParser.Parse(Samples.Analysis(status, complexity), Demo));

    [Fact]
    public void Missing_status_line_fails() =>
        Assert.Throws<FormatException>(() => AnalysisParser.Parse("**Complexity:** simple", Demo));

    [Fact]
    public void Plain_field_without_bold_is_read() =>
        Assert.Equal("ready", MarkdownSections.ReadField("Status: Ready\n", "Status"));

    [Fact]
    public void Components_affected_is_read_as_a_list_of_names()
    {
        var block = Samples.Analysis("ready", "complex", components: "backend, `frontend`");
        var result = AnalysisParser.Parse(block, BackendAndFrontend);

        Assert.Equal(["backend", "frontend"], result.Components);
    }

    [Fact]
    public void All_means_every_code_component()
    {
        var block = Samples.Analysis("ready", "complex", components: "all");
        var result = AnalysisParser.Parse(block, BackendAndFrontend);

        Assert.Equal(["backend", "frontend"], result.Components);
    }

    [Fact]
    public void Unknown_is_fine_while_questions_are_open_and_gives_no_components()
    {
        var block = Samples.Analysis("questions-open", "simple", components: "unknown");
        var result = AnalysisParser.Parse(block, BackendAndFrontend);

        Assert.Empty(result.Components);
    }

    [Fact]
    public void Unknown_with_status_ready_fails()
    {
        var block = Samples.Analysis("ready", "simple", components: "unknown");
        var exception = Assert.Throws<FormatException>(() => AnalysisParser.Parse(block, Demo));

        Assert.Contains("unknown", exception.Message);
    }

    [Theory]
    [InlineData("questions-open")]
    [InlineData("ready")]
    public void A_missing_components_line_fails(string status)
    {
        var block = Samples.Analysis(status, "simple").Replace("**Components affected:**", "**Area:**");

        Assert.Throws<FormatException>(() => AnalysisParser.Parse(block, Demo));
    }

    [Theory]
    [InlineData("backend | all")]       // agent copied the template
    [InlineData("<names, comma separated> | all")]
    [InlineData("issues")]              // exists in the project, but holds no code
    [InlineData("backend, shop")]
    [InlineData("all, backend")]
    [InlineData("")]
    public void A_name_that_is_not_a_code_component_fails(string components)
    {
        var block = Samples.Analysis("ready", "complex", components: components);

        Assert.Throws<FormatException>(() => AnalysisParser.Parse(block, BackendAndFrontend));
    }

    [Fact]
    public void More_than_one_component_with_simple_complexity_fails()
    {
        var block = Samples.Analysis("ready", "simple", components: "backend, frontend");
        var exception = Assert.Throws<FormatException>(() => AnalysisParser.Parse(block, BackendAndFrontend));

        Assert.Contains("complex", exception.Message);
    }

    [Fact]
    public void All_with_simple_complexity_fails_when_the_project_has_several_code_components()
    {
        var block = Samples.Analysis("ready", "simple", components: "all");

        Assert.Throws<FormatException>(() => AnalysisParser.Parse(block, BackendAndFrontend));
    }

    [Fact]
    public void All_with_simple_complexity_is_fine_when_the_project_has_one_code_component()
    {
        var block = Samples.Analysis("ready", "simple", components: "all");
        var result = AnalysisParser.Parse(block, Demo);

        Assert.Equal(["demo"], result.Components);
    }

    [Fact]
    public void One_component_with_complex_complexity_is_fine()
    {
        var block = Samples.Analysis("ready", "complex", components: "frontend");
        var result = AnalysisParser.Parse(block, BackendAndFrontend);

        Assert.Equal(Complexity.Complex, result.Complexity);
        Assert.Equal(["frontend"], result.Components);
    }
}

public class SpecParserTests
{
    private static readonly string[] BackendAndFrontend = ["backend", "frontend"];

    [Fact]
    public void Reads_the_component_of_every_step()
    {
        var spec = Samples.SpecFor("backend", "frontend");
        var steps = SpecParser.Parse(spec, BackendAndFrontend);

        Assert.Equal(["backend", "frontend"], steps.Select(step => step.Component));
        Assert.Equal("`src/Export2.cs`", steps[1].File);
        Assert.Equal("`src/Report2.cs`", steps[1].PatternFrom);
        Assert.Equal("Add the export code.", steps[1].Change);
    }

    [Fact]
    public void Extra_cells_join_the_change()
    {
        var spec = """
            ### Technical plan
            | Step | Component | File | Pattern from | Change |
            | --- | --- | --- | --- | --- |
            | 1 | backend | `a.cs` | `b.cs` | Add it | and more |
            """;

        var step = Assert.Single(SpecParser.Parse(spec, BackendAndFrontend));

        Assert.Equal("Add it | and more", step.Change);
    }

    [Fact]
    public void A_dash_is_a_valid_pattern_from()
    {
        var spec = """
            ### Technical plan
            | Step | Component | File | Pattern from | Change |
            | --- | --- | --- | --- | --- |
            | 1 | backend | `a.cs` | - | New kind of file; no file of this kind exists |
            """;

        var step = Assert.Single(SpecParser.Parse(spec, BackendAndFrontend));

        Assert.Equal("-", step.PatternFrom);
    }

    [Fact]
    public void An_empty_pattern_from_fails_with_the_step_number()
    {
        var spec = """
            ### Technical plan
            | Step | Component | File | Pattern from | Change |
            | --- | --- | --- | --- | --- |
            | 1 | backend | `a.cs` | `b.cs` | Add it |
            | 2 | frontend | `c.ts` |  | Add it |
            """;

        var exception = Assert.Throws<FormatException>(() => SpecParser.Parse(spec, BackendAndFrontend));

        Assert.Contains("step 2", exception.Message);
        Assert.Contains("Pattern from", exception.Message);
    }

    [Fact]
    public void The_four_column_table_of_the_old_layout_fails_and_names_pattern_from()
    {
        var spec = """
            ### Technical plan
            | Step | Component | File | Change |
            | --- | --- | --- | --- |
            | 1 | backend | `a.cs` | Add it. |
            """;

        var exception = Assert.Throws<FormatException>(() => SpecParser.Parse(spec, BackendAndFrontend));

        Assert.Contains("Pattern from", exception.Message);
        Assert.Contains("5 cells", exception.Message);
    }

    [Fact]
    public void A_component_name_is_matched_without_regard_to_case()
    {
        var steps = SpecParser.Parse(Samples.SpecFor("Backend"), BackendAndFrontend);

        Assert.Equal("backend", Assert.Single(steps).Component);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("backend, frontend")]
    [InlineData("issues")]
    [InlineData("")]
    public void A_step_with_a_bad_component_fails(string component) =>
        Assert.Throws<FormatException>(() => SpecParser.Parse(Samples.SpecFor(component), BackendAndFrontend));

    [Fact]
    public void The_old_three_column_table_fails()
    {
        var spec = """
            ### Technical plan
            | Step | File | Change |
            | --- | --- | --- |
            | 1 | `a.cs` | Add it. |
            """;

        var exception = Assert.Throws<FormatException>(() => SpecParser.Parse(spec, BackendAndFrontend));

        Assert.Contains("5 cells", exception.Message);
    }

    [Fact]
    public void A_missing_or_empty_technical_plan_fails()
    {
        Assert.Throws<FormatException>(() => SpecParser.Parse("### Goal\nExport.", BackendAndFrontend));
        Assert.Throws<FormatException>(
            () => SpecParser.Parse("### Technical plan\nNone\n\n### Data changes\nNone", BackendAndFrontend));
    }
}

public class SpecContractCheckTests
{
    private static readonly string[] Demo = ["demo"];

    [Fact]
    public void None_and_valid_tables_pass()
    {
        SpecParser.Check(Samples.SpecFor("demo"));
        SpecParser.Check(Samples.SpecWith(Demo, Samples.ApiTable, Samples.UiTable));
        SpecParser.Check(Samples.SpecWith(Demo, Samples.ApiTable, "None"));
    }

    [Fact]
    public void Extra_cells_join_the_last_column()
    {
        var api = Samples.ApiTable.TrimEnd() + " | and more |";

        SpecParser.Check(Samples.SpecWith(Demo, api, "None"));
    }

    [Theory]
    [InlineData("API changes")]
    [InlineData("UI changes")]
    public void A_missing_heading_fails_and_names_the_section(string heading)
    {
        var spec = Samples.SpecFor("demo").Replace($"### {heading}", "### Something else");

        var exception = Assert.Throws<FormatException>(() => SpecParser.Check(spec));

        Assert.Contains(heading, exception.Message);
    }

    [Fact]
    public void A_short_api_row_fails_and_names_the_section()
    {
        var shortApi = """
            | Method | Path | Query / paging | Request | Response | Errors | Auth |
            | --- | --- | --- | --- | --- | --- | --- |
            | GET | /api/exports | - | - | ExportRow |
            """;

        var exception = Assert.Throws<FormatException>(
            () => SpecParser.Check(Samples.SpecWith(Demo, shortApi, "None")));

        Assert.Contains("API changes", exception.Message);
        Assert.Contains("7 cells", exception.Message);
    }

    [Fact]
    public void A_short_ui_row_fails_and_names_the_section()
    {
        var shortUi = """
            | Route | Page / component | Copy from | Shared components | States |
            | --- | --- | --- | --- | --- |
            | /exports | ExportList | ReportList |
            """;

        var exception = Assert.Throws<FormatException>(
            () => SpecParser.Check(Samples.SpecWith(Demo, "None", shortUi)));

        Assert.Contains("UI changes", exception.Message);
        Assert.Contains("5 cells", exception.Message);
    }

    [Fact]
    public void Free_text_instead_of_a_table_fails()
    {
        var exception = Assert.Throws<FormatException>(
            () => SpecParser.Check(Samples.SpecWith(Demo, "- `GET /api/exports` -> response `Row`.", "None")));

        Assert.Contains("API changes", exception.Message);
    }
}

public class BreakdownParserTests
{
    private static readonly string[] Demo = ["demo"];
    private static readonly string[] BackendAndFrontend = ["backend", "frontend"];

    [Fact]
    public void Reads_rows_dependencies_developer_tag_and_contract()
    {
        var block = Samples.Analysis("ready", "complex", Samples.BreakdownTable);
        var breakdown = BreakdownParser.Parse(block, Demo);

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
            () => BreakdownParser.Parse(Samples.Analysis("ready", "complex"), Demo));

        Assert.Contains("None", exception.Message);
    }

    [Fact]
    public void Wrong_numbering_fails() =>
        Assert.Throws<FormatException>(() => BreakdownParser.Parse(Samples.Analysis(
            "ready",
            "complex",
            Samples.BreakdownTable.Replace("| 2 | demo | Export command", "| 5 | demo | Export command")),
            Demo));

    [Fact]
    public void Missing_shared_contract_fails() =>
        Assert.Throws<FormatException>(() => BreakdownParser.Parse(Samples.Analysis(
            "ready",
            "complex",
            Samples.BreakdownTable.Replace("Shared contract:", "Contract -")),
            Demo));

    [Fact]
    public void Wrong_column_count_fails() =>
        Assert.Throws<FormatException>(() => BreakdownParser.Parse(Samples.Analysis(
            "ready",
            "complex",
            "| # | Sub-task | Touches | Depends on |\n| --- | --- | --- | --- |\n| 1 | A - scope | x | - |\nShared contract: X"),
            Demo));

    [Fact]
    public void The_old_five_column_table_fails_with_the_new_layout_in_the_message()
    {
        var oldTable = "| # | Title | Scope | Touches | Depends on |\n| --- | --- | --- | --- | --- |\n" +
                       "| 1 | A | scope | x | - |\nShared contract: X";

        var exception = Assert.Throws<FormatException>(
            () => BreakdownParser.Parse(Samples.Analysis("ready", "complex", oldTable), Demo));

        Assert.Contains("6 cells", exception.Message);
        Assert.Contains("Component", exception.Message);
    }

    [Fact]
    public void Every_row_names_its_component()
    {
        var table = Samples.Breakdown("backend", "frontend", "backend");
        var breakdown = BreakdownParser.Parse(Samples.Analysis("ready", "complex", table), BackendAndFrontend);

        Assert.Equal(["backend", "frontend", "backend"], breakdown.Tasks.Select(task => task.Component));
        Assert.Equal("Export entity", breakdown.Tasks[0].Title);
        Assert.Equal("Export API", breakdown.Tasks[2].Title);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("backend, frontend")]
    [InlineData("issues")]
    [InlineData("shop")]
    [InlineData("")]
    public void A_row_with_a_bad_component_fails(string component)
    {
        var table = Samples.Breakdown("backend", component, "frontend");
        var block = Samples.Analysis("ready", "complex", table);

        var exception = Assert.Throws<FormatException>(() => BreakdownParser.Parse(block, BackendAndFrontend));

        Assert.Contains("row 2", exception.Message);
    }
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
    public void A_convention_finding_is_accepted_and_counted()
    {
        var result = ReviewParser.Parse(Samples.Review(severity: "convention"));

        Assert.Equal((0, 0, 1, 1), (result.Bugs, result.Risks, result.Conventions, result.Style));
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
    private static readonly SubTask Task2 =
        new(2, "backend", "Export command", "Add the command.", "src/App", [1], false);

    [Fact]
    public void Build_and_parse_round_trip()
    {
        var draft = new SubIssueDraft(12, Task2, 3, "entity Export");
        var body = SubIssueBody.Build(draft);
        var info = SubIssueBody.Parse(body);

        Assert.Equal(new SubIssueInfo(12, 2, 3, "feat/issue-12", "backend"), info);
        Assert.Contains("Depends on: #12.1", body);
        Assert.Contains("entity Export", body);
        Assert.Equal("#12.2 Export command", SubIssueBody.Title(12, Task2));
    }

    [Fact]
    public void Build_writes_the_parent_and_component_lines_next_to_each_other()
    {
        var draft = new SubIssueDraft(12, Task2, 3, "entity Export");
        var lines = SubIssueBody.Build(draft).Split('\n');

        Assert.Equal("Parent: #12", lines[0].TrimEnd());
        Assert.Equal("Component: backend", lines[1].TrimEnd());
        Assert.Equal("Order: 2 of 3", lines[2].TrimEnd());
    }

    [Fact]
    public void A_developer_sub_issue_says_it_is_done_when_closed_and_names_the_branch_that_counts()
    {
        var developerTask = Task2 with { NeedsDeveloper = true };
        var body = SubIssueBody.Build(new SubIssueDraft(12, developerTask, 3, "entity Export"));

        Assert.Contains("close this issue", body);
        Assert.Contains("`ai/issue-12-2` into `feat/issue-12`", body);
        Assert.DoesNotContain("merge it into", body);
    }

    [Fact]
    public void A_sub_issue_written_before_components_existed_has_no_component()
    {
        var body = "Parent: #12\nOrder: 1 of 2\nBase branch: feat/issue-12\nDepends on: -\n\n### Scope\nx";

        Assert.Null(SubIssueBody.Parse(body)!.Component);
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
