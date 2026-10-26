using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

/// <summary>What a sub-issue and its PR see of the parent SPEC, and which branch the other repos are read at.</summary>
public class ContractContextTests
{
    private const string ParentSpec = "## SPEC - issue #12: Export\n\n### API changes\nNone";

    private static HostIssue SubIssue()
    {
        var body = "Parent: #12\nComponent: demo\n\n### Scope\nDo it.\n\n" +
                   "### Shared contract (exact names, used by every sub-issue)\nentity Export";

        return new HostIssue(21, "#12.1 Task", body, new HostUser("alice"), [], DateTimeOffset.UtcNow, null, "open");
    }

    private static HostPull SubIssuePull()
    {
        var head = new HostRef("ai/issue-12-1", "sha");
        var featureBase = new HostRef("feat/issue-12", "b");

        return new HostPull(30, "T", head, featureBase, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void A_sub_issue_plan_carries_the_parent_spec_and_the_binding_paragraph()
    {
        var plan = ContextBuilder.PlanFromSubIssue(SubIssue(), 12, ParentSpec);

        Assert.Contains("# Plan for sub-issue #21: #12.1 Task", plan);
        Assert.Contains("### Scope", plan);
        Assert.Contains("## Parent SPEC (issue #12): the whole feature; your sub-issue is one part of it", plan);
        Assert.Contains(ParentSpec, plan);
        Assert.Contains("are binding for every sub-issue", plan);
        Assert.Contains("Build only your `### Scope`", plan);
    }

    [Fact]
    public void With_a_parent_spec_the_bodys_own_shared_contract_is_left_out()
    {
        var plan = ContextBuilder.PlanFromSubIssue(SubIssue(), 12, ParentSpec);

        Assert.DoesNotContain("used by every sub-issue", plan);
        Assert.DoesNotContain("entity Export", plan);
        Assert.Contains("Do it.", plan);
    }

    private const string TwoComponentSpec =
        "## SPEC - issue #12: Export\n\n" +
        "### Technical plan\n" +
        "| Step | Component | File | Pattern from | Change |\n" +
        "| --- | --- | --- | --- | --- |\n" +
        "| 1 | backend | `Api/Export.cs` | `Api/Report.cs` | Endpoint. |\n" +
        "| 2 | frontend | `src/export.page.ts` | `src/report.page.ts` | Page. |\n" +
        "| 3 | backend | `Api/ExportTests.cs` | `Api/ReportTests.cs` | Tests. |\n\n" +
        "### API changes\n" +
        "| Method | Path | Query / paging | Request | Response | Errors | Auth |\n" +
        "| --- | --- | --- | --- | --- | --- | --- |\n" +
        "| GET | /api/export | - | - | ExportDto | 404 | read |\n";

    private static HostIssue SubIssueOf(string component)
    {
        var body = $"Parent: #12\nComponent: {component}\nOrder: 2 of 3\n\n### Scope\nDo it.";

        return new HostIssue(22, "#12.2 Page", body, new HostUser("alice"), [], DateTimeOffset.UtcNow, null, "open");
    }

    [Fact]
    public void A_sub_issue_plan_keeps_only_the_technical_plan_steps_of_its_component()
    {
        var plan = ContextBuilder.PlanFromSubIssue(SubIssueOf("frontend"), 12, TwoComponentSpec);

        Assert.Contains("| 2 | frontend | `src/export.page.ts`", plan);
        Assert.DoesNotContain("Api/Export.cs", plan);
        Assert.DoesNotContain("Api/ExportTests.cs", plan);
        Assert.Contains("| Step | Component | File | Pattern from | Change |", plan);
        Assert.Contains("| GET | /api/export |", plan);
        Assert.Contains("shows only the steps of component `frontend`", plan);
    }

    [Fact]
    public void A_sub_issue_plan_marks_its_own_steps_and_keeps_the_others_of_its_component()
    {
        var spec = TwoComponentSpec.Replace(
            "| 3 | backend | `Api/ExportTests.cs`",
            "| 4 | backend | `Api/Audit.cs` | `Api/Report.cs` | Audit. |\n| 3 | backend | `Api/ExportTests.cs`");
        var body = "Parent: #12\nComponent: backend\nOrder: 2 of 3\nSteps: 1, 3\n\n### Scope\nDo it.";
        var withSteps = SubIssueOf("backend") with { Body = body };

        var plan = ContextBuilder.PlanFromSubIssue(withSteps, 12, spec);

        Assert.Contains("| 1 (yours) | backend | `Api/Export.cs` |", plan);
        Assert.Contains("| 3 (yours) | backend | `Api/ExportTests.cs` |", plan);
        Assert.Contains("| 4 | backend | `Api/Audit.cs` |", plan);
        Assert.DoesNotContain("src/export.page.ts", plan);
        Assert.Contains("Your steps: 1, 3 (marked `(yours)`)", plan);
    }

    [Fact]
    public void A_sub_issue_without_a_steps_line_marks_nothing()
    {
        var plan = ContextBuilder.PlanFromSubIssue(SubIssueOf("frontend"), 12, TwoComponentSpec);

        Assert.DoesNotContain("(yours)", plan);
        Assert.DoesNotContain("Your steps:", plan);
    }

    [Theory]
    [InlineData("mobile")]     // no step names it: the whole plan stays
    [InlineData("Backend")]    // case does not matter
    public void A_sub_issue_plan_keeps_the_whole_technical_plan_when_no_step_or_every_step_is_left(string component)
    {
        var spec = component == "mobile"
            ? TwoComponentSpec
            : TwoComponentSpec.Replace("| 2 | frontend |", "| 2 | backend |");

        var plan = ContextBuilder.PlanFromSubIssue(SubIssueOf(component), 12, spec);

        Assert.Contains("Api/Export.cs", plan);
        Assert.Contains("Api/ExportTests.cs", plan);
        Assert.DoesNotContain("shows only the steps", plan);
    }

    [Fact]
    public void A_parent_without_a_spec_says_so_and_still_gives_the_binding_paragraph()
    {
        var plan = ContextBuilder.PlanFromSubIssue(SubIssue(), 12, null);

        Assert.Contains("None: the parent issue has no SPEC (analysed before this version)", plan);
        Assert.Contains("are binding for every sub-issue", plan);
        Assert.Contains("entity Export", plan);
    }

    [Fact]
    public void Without_a_parent_number_only_the_body_is_written()
    {
        var plan = ContextBuilder.PlanFromSubIssue(SubIssue(), null, null);

        Assert.DoesNotContain("Parent SPEC", plan);
        Assert.Contains("### Scope", plan);
    }

    [Fact]
    public void A_pr_of_a_sub_issue_prints_the_parent_in_the_spec_heading()
    {
        var pull = SubIssuePull();
        var linked = new LinkedIssue(21, null, "the plan") { ParentNumber = 12 };

        var text = ContextBuilder.ForPull(pull, [], linked);

        Assert.Contains("## Linked issue: SPEC (from parent #12)", text);
    }

    [Fact]
    public void A_pr_of_a_simple_issue_keeps_the_plain_spec_heading()
    {
        var pull = new HostPull(
            30,
            "T",
            new HostRef("ai/issue-12", "sha"),
            new HostRef("main", "b"),
            DateTimeOffset.UtcNow);
        var linked = new LinkedIssue(12, null, "the spec");

        var text = ContextBuilder.ForPull(pull, [], linked);

        Assert.Contains("## Linked issue: SPEC\n".ReplaceLineEndings(), text.ReplaceLineEndings());
        Assert.DoesNotContain("from parent", text);
    }

    [Fact]
    public void A_review_fix_plan_names_the_parent_issue()
    {
        var pull = SubIssuePull();
        var linked = new LinkedIssue(21, null, "the plan") { ParentNumber = 12 };
        var input = new ReviewFixPlanInput(pull, null, null, [], 1, linked);

        var plan = ContextBuilder.PlanForReviewFix(input);

        Assert.Contains("## Plan of the parent issue #12 (the linked issue #21 is a sub-issue;", plan);
    }

    [Theory]
    [InlineData("feat/issue-12", true)]
    [InlineData("ai/issue-12-1", false)]
    [InlineData("main", false)]
    public void A_feature_branch_is_feat_issue(string branch, bool expected) =>
        Assert.Equal(expected, Branches.IsFeature(branch));

    [Theory]
    [InlineData("origin/feat/issue-12", "feat/issue-12")]
    [InlineData("origin/main", "main")]
    [InlineData("main", "main")]
    public void A_local_name_is_a_remote_ref_without_origin(string remoteRef, string expected) =>
        Assert.Equal(expected, Branches.LocalName(remoteRef));

    [Theory]
    [InlineData("ai/issue-12-1", "feat/issue-12", "feat/issue-12")]   // a sub-issue PR: its base
    [InlineData("feat/issue-12", "main", "feat/issue-12")]            // the final PR: its head
    [InlineData("ai/issue-12", "main", null)]                          // a simple issue
    [InlineData("topic", "release", null)]
    public void The_feature_branch_of_a_pr_is_its_base_else_its_head(string head, string baseRef, string? expected)
    {
        var pull = new HostPull(30, "T", new HostRef(head, "sha"), new HostRef(baseRef, "b"), DateTimeOffset.UtcNow);

        Assert.Equal(expected, Branches.FeatureBranchOf(pull));
    }

    [Fact]
    public void Components_md_says_where_each_repo_was_checked_out()
    {
        var project = TestProjects.SeparateIssuesRepo();
        var root = Path.Combine(Path.GetTempPath(), "work", "mbh");
        var input = new ComponentsDocInput(project, root, "org/mbh-frontend", "frontend", true, _ => false)
        {
            CheckedOutAt = new Dictionary<string, string>
            {
                ["org/mbh-issues"] = "origin/main",
                ["org/mbh-backend"] = "origin/feat/issue-12",
                ["org/mbh-frontend"] = "origin/feat/issue-12",
            },
        };

        var text = ComponentsDoc.Build(input);

        Assert.Contains(
            "Checked out at: org/mbh-backend at origin/feat/issue-12 (the feature branch of the parent issue)",
            text);
        Assert.Contains("Checked out at: org/mbh-issues at origin/main (default branch)", text);
        Assert.Contains(
            "Checked out at: org/mbh-frontend at origin/feat/issue-12 (the start point of this job)",
            text);
    }
}
