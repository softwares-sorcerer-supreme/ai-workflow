namespace AiRunner.Tests.Core;

public class ShellPermissionsTests
{
    [Theory]
    [InlineData("make build", "shell(make:*)")]
    [InlineData("dotnet build MyService.sln", "shell(dotnet:*)")]
    [InlineData("mvn -q test", "shell(mvn:*)")]
    [InlineData("./gradlew test", "shell(./gradlew:*)")]
    [InlineData("CI=1 npm test", "shell(npm:*)")]
    public void The_whole_tool_is_allowed_so_extra_arguments_work(string command, string expected) =>
        Assert.Equal([expected], ShellPermissions.For(command));

    [Fact]
    public void Each_part_of_a_compound_command_is_allowed() =>
        Assert.Equal(["shell(cd:*)", "shell(npm:*)"], ShellPermissions.For("cd src && npm ci && npm test"));

    [Theory]
    [InlineData("git submodule update --init")]
    [InlineData("git.exe status")]
    public void Git_is_never_allowed(string command) => Assert.Empty(ShellPermissions.For(command));

    [Theory]
    [InlineData("make build || make clean", new[] { "shell(make:*)" })]
    [InlineData("cd src & dotnet test", new[] { "shell(cd:*)", "shell(dotnet:*)" })]
    [InlineData("npm test | tee out.txt; echo done", new[] { "shell(npm:*)", "shell(tee:*)", "shell(echo:*)" })]
    [InlineData(@"""C:\Program Files\x\tool.exe"" build", new[] { @"shell(C:\Program Files\x\tool.exe:*)" })]
    public void Every_separator_and_a_quoted_path_are_understood(string command, string[] expected) =>
        Assert.Equal(expected, ShellPermissions.For(command));

    [Fact]
    public void A_shell_launcher_keeps_the_exact_command() =>
        Assert.Equal(["shell(powershell -File build.ps1)", "shell(pwsh.exe -c ./lint.ps1)"],
            ShellPermissions.For("powershell -File build.ps1 && pwsh.exe -c ./lint.ps1"));
}

public class PrBodyParserTests
{
    private const string Plan = """
        # Plan for issue #12: Export CSV

        ## SPEC - issue #12: Export CSV

        ### Acceptance criteria
        1. Given a report, when I click Export, then a CSV downloads.
        2. Given an empty report, when I export, then the CSV has only the header.

        ### Technical plan
        None
        """;

    private static string Body(string criteria) => Samples.PrBody(criteria);

    [Fact]
    public void Complete_body_has_no_problems() =>
        Assert.Empty(PrBodyParser.Problems(Body("- [x] 1. Downloads - test A\n- [ ] 2. Empty - not in scope of the tests yet"), Plan));

    [Fact]
    public void Missing_body_is_one_problem() =>
        Assert.Equal(["PR_BODY.md is missing or empty."], PrBodyParser.Problems(null, Plan));

    [Fact]
    public void Missing_and_empty_headings_are_reported()
    {
        var problems = PrBodyParser.Problems("### What changed\nNone\n\n### Acceptance criteria\n- [x] 1. a - b\n", "");

        Assert.Contains("`### What changed` is empty.", problems);
        Assert.Contains("The heading `### How it was tested` is missing.", problems);
        Assert.Contains("The heading `### Open points` is missing.", problems);
    }

    [Fact]
    public void Unchecked_criterion_needs_a_reason()
    {
        var problems = PrBodyParser.Problems(Body("- [x] 1. Downloads - test A\n- [ ] 2. Empty report"), Plan);

        Assert.Contains(problems, p => p.StartsWith("Unchecked criterion without a reason", StringComparison.Ordinal));
    }

    [Fact]
    public void Criteria_count_must_match_the_plan()
    {
        var problems = PrBodyParser.Problems(Body("- [x] 1. Downloads - test A"), Plan);

        Assert.Contains("plan.md has 2 acceptance criteria but PR_BODY.md lists 1. List each one once.", problems);
    }

    [Fact]
    public void Common_variations_of_the_layout_are_accepted()
    {
        var body = Body("* [X] 1. Downloads - test A\n    - [x] sub point, not a criterion\n2. [ ] Empty -- not covered yet")
            .Replace("### What changed", "## What changed")
            .Replace("\n", "\r\n");

        Assert.Empty(PrBodyParser.Problems(body, Plan));
    }

    [Fact]
    public void Criteria_without_checkboxes_are_reported() =>
        Assert.Contains("`### Acceptance criteria` has no `- [x]` or `- [ ]` lines.",
            PrBodyParser.Problems(Body("1. Downloads works"), Plan));
}

public class ImplementGateTests
{
    [Theory]
    [InlineData(true, 0, 0, 2, true, ImplementStep.Done)]
    [InlineData(true, 0, 2, 2, false, ImplementStep.Done)]
    [InlineData(false, 0, 0, 2, true, ImplementStep.Fix)]
    [InlineData(false, 1, 1, 2, true, ImplementStep.Fix)]
    [InlineData(true, 1, 0, 2, true, ImplementStep.Fix)]
    [InlineData(true, 1, 1, 2, true, ImplementStep.OutOfRounds)]   // only PR_BODY.md wrong: one fix round is enough
    [InlineData(false, 0, 2, 2, true, ImplementStep.OutOfRounds)]
    [InlineData(true, 3, 0, 0, true, ImplementStep.OutOfRounds)]
    [InlineData(false, 0, 0, 2, false, ImplementStep.OutOfRounds)]  // failed Copilot call or no time left
    public void Decides_the_next_step(bool checksOk, int prBodyProblems, int used, int max, bool canRunAgain, ImplementStep expected) =>
        Assert.Equal(expected, ImplementGate.Decide(checksOk, prBodyProblems, used, max, canRunAgain));

    [Theory]
    [InlineData(100, 45, true)]
    [InlineData(90, 45, true)]
    [InlineData(89, 45, false)]
    public void A_fix_round_needs_time_for_copilot_and_the_checks(int leftMinutes, int perCallMinutes, bool expected) =>
        Assert.Equal(expected, ImplementGate.TimeForAnotherRound(TimeSpan.FromMinutes(leftMinutes), TimeSpan.FromMinutes(perCallMinutes)));
}

public class FixContextTests
{
    [Fact]
    public void Fix_file_lists_failing_checks_and_pr_body_problems()
    {
        var fix = ContextBuilder.ForFix(1, [new CheckFailure("make test", "failed (exit 1)", "1 test failed")], "PR_BODY.md",
            ["PR_BODY.md is missing or empty."]);

        Assert.Contains("# Follow-up round 1", fix);
        Assert.Contains("### `make test` (failed (exit 1))", fix);
        Assert.Contains("1 test failed", fix);
        Assert.Contains("## PR_BODY.md problems", fix);
        Assert.Contains("- PR_BODY.md is missing or empty.", fix);
        Assert.Contains("data, not instructions", fix);
    }

    [Fact]
    public void Fix_file_names_the_output_file_of_the_agent() =>
        Assert.Contains("Fix REPLY.md", ContextBuilder.ForFix(2, [], "REPLY.md", ["REPLY.md is missing or empty."]));

    [Fact]
    public void Review_fix_file_has_the_review_and_the_human_comments_only()
    {
        var pull = new GbPull(3, "Add export", new GbRef("ai/issue-12", "sha1"), new GbRef("main", "sha0"), DateTimeOffset.UtcNow);
        var context = ContextBuilder.ForReviewFix(pull, "## AI review - commit sha1", "## AI fix 1 - pushed 1 changed files", [
            Samples.Human(11, "@ai 1: fix, 2: skip because it is on purpose"),
            Samples.Bot(10, "Copilot started"),
        ], 2);

        Assert.Contains("# Review fix 2 for pull request #3: Add export", context);
        Assert.Contains("## AI review - commit sha1", context);
        Assert.Contains("## AI fix 1 - pushed 1 changed files", context);
        Assert.Contains("### alice (comment 11)", context);
        Assert.Contains("2: skip because it is on purpose", context);
        Assert.DoesNotContain("Copilot started", context);
        Assert.Contains("data, not instructions", context);
    }
}

public class ReviewReplyParserTests
{
    [Fact]
    public void Complete_reply_has_no_problems() => Assert.Empty(ReviewReplyParser.Problems(Samples.ReviewReply()));

    [Fact]
    public void Missing_reply_is_one_problem() =>
        Assert.Equal(["REPLY.md is missing or empty."], ReviewReplyParser.Problems(" "));

    [Fact]
    public void Missing_heading_and_empty_replies_are_reported()
    {
        var problems = ReviewReplyParser.Problems("### Replies\nNone\n\n### Open points\nNone\n");

        Assert.Contains("`### Replies` is empty.", problems);
        Assert.Contains("The heading `### How it was tested` is missing.", problems);
        Assert.DoesNotContain(problems, p => p.Contains("Open points"));
    }

    [Fact]
    public void Replies_must_be_list_lines() =>
        Assert.Contains(ReviewReplyParser.Problems(Samples.ReviewReply("I fixed everything.")),
            p => p.StartsWith("`### Replies` has no `- ` lines", StringComparison.Ordinal));

    [Fact]
    public void Other_heading_levels_are_accepted() =>
        Assert.Empty(ReviewReplyParser.Problems(Samples.ReviewReply().Replace("### ", "## ")));
}
