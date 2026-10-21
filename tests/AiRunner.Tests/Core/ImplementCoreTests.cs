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
    public void A_lint_that_chains_two_tools_allows_both() =>
        Assert.Equal(["shell(npm:*)", "shell(npx:*)"], ShellPermissions.For("npm run lint && npx prettier --check ."));

    [Fact]
    public void A_shell_launcher_keeps_the_exact_command() =>
        Assert.Equal(
            ["shell(powershell -File build.ps1)", "shell(pwsh.exe -c ./lint.ps1)"],
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
    public void Complete_body_has_no_problems()
    {
        var body = Body("- [x] 1. Downloads - test A\n- [ ] 2. Empty - not in scope of the tests yet");

        Assert.Empty(PrBodyParser.Problems(body, Plan));
    }

    [Fact]
    public void Missing_body_is_one_problem() =>
        Assert.Equal(["PR_BODY.md is missing or empty."], PrBodyParser.Problems(null, Plan));

    [Fact]
    public void Missing_and_empty_headings_are_reported()
    {
        var problems = PrBodyParser.Problems(
            "### What changed\nNone\n\n### Acceptance criteria\n- [x] 1. a - b\n",
            "");

        Assert.Contains("`### What changed` is empty.", problems);
        Assert.Contains("The heading `### How it was tested` is missing.", problems);
        Assert.Contains("The heading `### Open points` is missing.", problems);
    }

    [Fact]
    public void Unchecked_criterion_needs_a_reason()
    {
        var body = Body("- [x] 1. Downloads - test A\n- [ ] 2. Empty report");
        var problems = PrBodyParser.Problems(body, Plan);

        Assert.Contains(
            problems,
            problem => problem.StartsWith("Unchecked criterion without a reason", StringComparison.Ordinal));
    }

    [Fact]
    public void Criteria_count_must_match_the_plan()
    {
        var body = Body("- [x] 1. Downloads - test A");
        var problems = PrBodyParser.Problems(body, Plan);

        Assert.Contains("plan.md has 2 acceptance criteria but PR_BODY.md lists 1. List each one once.", problems);
    }

    [Fact]
    public void Common_variations_of_the_layout_are_accepted()
    {
        var body = Body(
                "* [X] 1. Downloads - test A\n    - [x] sub point, not a criterion\n2. [ ] Empty -- not covered yet")
            .Replace("### What changed", "## What changed")
            .Replace("\n", "\r\n");

        Assert.Empty(PrBodyParser.Problems(body, Plan));
    }

    [Fact]
    public void Criteria_without_checkboxes_are_reported()
    {
        var body = Body("1. Downloads works");

        Assert.Contains(
            "`### Acceptance criteria` has no `- [x]` or `- [ ]` lines.",
            PrBodyParser.Problems(body, Plan));
    }
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
    public void Decides_the_next_step(
        bool checksOk,
        int prBodyProblems,
        int fixRoundsUsed,
        int maxFixRounds,
        bool canRunAgain,
        ImplementStep expected)
    {
        var progress = new ImplementProgress(checksOk, prBodyProblems, fixRoundsUsed, maxFixRounds, canRunAgain);
        var step = ImplementGate.Decide(progress);

        Assert.Equal(expected, step);
    }

    [Theory]
    [InlineData(false, 0, 1, 5, ImplementStep.NoProgress)]   // checks fail the same way as the round before
    [InlineData(true, 2, 1, 5, ImplementStep.OutOfRounds)]   // checks pass: the one PR_BODY.md round rule says it
    [InlineData(false, 0, 0, 5, ImplementStep.Fix)]          // round 1 has no round before it
    [InlineData(false, 0, 5, 5, ImplementStep.OutOfRounds)]  // out of rounds wins
    public void A_round_that_ends_like_the_one_before_stops_the_rounds(
        bool checksOk,
        int prBodyProblems,
        int fixRoundsUsed,
        int maxFixRounds,
        ImplementStep expected)
    {
        var progress = new ImplementProgress(checksOk, prBodyProblems, fixRoundsUsed, maxFixRounds, CanRunAgain: true)
        {
            IsSameAsLastRound = true,
        };

        Assert.Equal(expected, ImplementGate.Decide(progress));
    }

    [Fact]
    public void A_fingerprint_ignores_times_and_durations_but_not_what_failed()
    {
        static string Of(string output) =>
            ImplementGate.Fingerprint([new CheckFailure("make test", "failed (exit 1)", output)], []);

        var first = Of("2026-10-20T06:18:46Z Failed: 3, Passed: 9, Duration: 2.5 s\nElapsed 00:00:12.34 (450 ms)");
        var sameRun = Of("2026-10-20T07:01:02Z Failed: 3, Passed: 9, Duration: 3 s\nElapsed 00:00:15.01 (90 ms)");
        var fewerFailures = Of("2026-10-20T07:01:02Z Failed: 2, Passed: 10, Duration: 3 s\nElapsed 00:00:15 (9 ms)");

        Assert.Equal(first, sameRun);
        Assert.NotEqual(first, fewerFailures);
        Assert.NotEqual(
            ImplementGate.Fingerprint([], ["PR_BODY.md is missing."]),
            ImplementGate.Fingerprint([], []));
    }

    [Theory]
    [InlineData(100, 45, true)]
    [InlineData(90, 45, true)]
    [InlineData(89, 45, false)]
    public void A_fix_round_needs_time_for_copilot_and_the_checks(
        int leftMinutes,
        int perCallMinutes,
        bool expected)
    {
        var left = TimeSpan.FromMinutes(leftMinutes);
        var perCall = TimeSpan.FromMinutes(perCallMinutes);

        Assert.Equal(expected, ImplementGate.TimeForAnotherRound(left, perCall));
    }
}

public class FixContextTests
{
    [Fact]
    public void Fix_file_lists_failing_checks_and_pr_body_problems()
    {
        var failure = new CheckFailure("make test", "failed (exit 1)", "1 test failed");
        var input = new FixRoundInput(
            Round: 1,
            Failures: [failure],
            OutputFile: "PR_BODY.md",
            OutputProblems: ["PR_BODY.md is missing or empty."]);
        var fixFile = ContextBuilder.ForFix(input);

        Assert.Contains("# Follow-up round 1", fixFile);
        Assert.Contains("### `make test` (failed (exit 1))", fixFile);
        Assert.Contains("1 test failed", fixFile);
        Assert.Contains("## PR_BODY.md problems", fixFile);
        Assert.Contains("- PR_BODY.md is missing or empty.", fixFile);
        Assert.Contains("data, not instructions", fixFile);
    }

    [Fact]
    public void Fix_file_names_the_output_file_of_the_agent()
    {
        var input = new FixRoundInput(
            Round: 2,
            Failures: [],
            OutputFile: "PR_BODY.md",
            OutputProblems: ["PR_BODY.md is missing or empty."]);
        var fixFile = ContextBuilder.ForFix(input);

        Assert.Contains("Fix PR_BODY.md", fixFile);
    }

    [Fact]
    public void Review_fix_plan_has_the_review_the_human_comments_and_the_linked_plan()
    {
        var pull = new HostPull(
            3,
            "Add export",
            new HostRef("ai/issue-12", "sha1"),
            new HostRef("main", "sha0"),
            DateTimeOffset.UtcNow);

        var humanComment = Samples.Human(11, "@ai 1: fix, 2: skip because it is on purpose");
        var botComment = Samples.Bot(10, "Copilot started");
        var linked = new LinkedIssue(Number: 12, Analysis: null, Spec: Samples.Spec);
        var input = new ReviewFixPlanInput(
            Pull: pull,
            Review: "## AI review - commit sha1",
            LastReply: "## AI fix 1 - pushed 1 changed files",
            Requests: [humanComment, botComment],
            FixNumber: 2,
            Linked: linked);
        var plan = ContextBuilder.PlanForReviewFix(input);

        Assert.StartsWith("# Plan: review fix 2 for pull request #3: Add export", plan);
        Assert.Contains("## AI review - commit sha1", plan);
        Assert.Contains("## AI fix 1 - pushed 1 changed files", plan);
        Assert.Contains("### alice (comment 11)", plan);
        Assert.Contains("2: skip because it is on purpose", plan);
        Assert.DoesNotContain("Copilot started", plan);
        Assert.Contains("data, not instructions", plan);
        Assert.Contains("## Plan of the linked issue #12", plan);
        Assert.Contains("SPEC - issue #12", plan);
    }

    [Fact]
    public void Review_fix_plan_without_a_linked_plan_says_none()
    {
        var pull = new HostPull(3, "x", new HostRef("a", "s"), new HostRef("main", "t"), DateTimeOffset.UtcNow);
        var input = new ReviewFixPlanInput(
            Pull: pull,
            Review: null,
            LastReply: null,
            Requests: [],
            FixNumber: 1,
            Linked: new LinkedIssue(Number: null, Analysis: null, Spec: null));
        var plan = ContextBuilder.PlanForReviewFix(input);

        Assert.Contains("None: the pull request has no linked issue", plan);
    }
}
