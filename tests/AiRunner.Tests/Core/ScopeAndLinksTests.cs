using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Core;

/// <summary>The path rules of the scope guard.</summary>
public class ScopeRulesTests
{
    [Theory]
    [InlineData("migrations/", "src/db/migrations/001.sql", true)]
    [InlineData("migrations/", "migrations", false)]                  // a file with that name is not a folder
    [InlineData("*.secrets.json", "config/app.secrets.json", true)]
    [InlineData("*.secrets.json", "config/app.secrets.json.bak", false)]
    [InlineData("Jenkinsfile", "ci/Jenkinsfile", true)]
    [InlineData("Jenkinsfile", "Jenkinsfile.old", false)]
    [InlineData("JENKINSFILE", "ci/jenkinsfile", true)]                // case does not matter
    [InlineData(".github/", ".github/workflows/ci.yml", true)]
    [InlineData("src/gen/*.cs", "src/gen/a.cs", true)]                 // a "/" in the middle anchors at the root
    [InlineData("src/gen/*.cs", "other/src/gen/a.cs", false)]
    [InlineData("src/gen/*.cs", "src/gen/deep/a.cs", false)]           // "*" stays inside one name
    [InlineData("src/**/gen/", "src/a/b/gen/x.cs", true)]              // "**" crosses folders
    [InlineData("/Jenkinsfile", "Jenkinsfile", true)]
    [InlineData("/Jenkinsfile", "ci/Jenkinsfile", false)]
    [InlineData("docs/guide", "docs/guide/a.md", true)]                // a matched folder covers what is below it
    [InlineData("", "a.txt", false)]
    public void Never_edit_patterns_follow_gitignore_rules(string pattern, string path, bool expected) =>
        Assert.Equal(expected, ScopeRules.MatchesNeverEdit(path, [pattern]));

    [Fact]
    public void Any_pattern_of_the_list_is_enough() =>
        Assert.True(ScopeRules.MatchesNeverEdit("src/Jenkinsfile", ["migrations/", "Jenkinsfile"]));

    [Theory]
    [InlineData(".", "any/path.txt", true)]
    [InlineData("apps/web", "apps/web/src/a.ts", true)]
    [InlineData("apps/web", "apps/web", true)]
    [InlineData("apps/web", "apps/webshop/a.ts", false)]               // a longer name is not inside
    [InlineData("apps/web", "apps/api/a.ts", false)]
    [InlineData("apps/web", "APPS/Web/a.ts", true)]
    [InlineData("apps/web", "README.md", false)]
    public void A_path_is_inside_a_component_folder(string componentPath, string path, bool expected) =>
        Assert.Equal(expected, ScopeRules.IsInside(path, componentPath));

    [Fact]
    public void A_read_only_clone_loses_every_change()
    {
        var undone = ScopeRules.ToUndo("org/other", ["a.txt", "b/c.txt"], null);

        Assert.Equal(["a.txt", "b/c.txt"], undone.Select(change => change.Path));
        Assert.All(undone, change => Assert.Equal(ScopeRules.ReadOnlyReason, change.Reason));
        Assert.All(undone, change => Assert.Equal("org/other", change.Repo));
    }

    [Fact]
    public void The_target_clone_keeps_changes_inside_the_component_and_loses_the_rest()
    {
        var scope = new WriteScope(["apps/api"], ["migrations/"]);
        string[] changed =
        [
            "apps/api/src/a.cs",
            "apps/web/src/b.ts",
            "README.md",
            "apps/api/migrations/001.sql",
        ];

        var undone = ScopeRules.ToUndo("org/mono", changed, scope);

        Assert.Equal(
            [("apps/web/src/b.ts", ScopeRules.OutsideReason),
             ("README.md", ScopeRules.OutsideReason),
             ("apps/api/migrations/001.sql", ScopeRules.NeverEditReason)],
            undone.Select(change => (change.Path, change.Reason)));
    }

    [Fact]
    public void Several_writable_components_allow_all_their_paths()
    {
        var scope = new WriteScope(["apps/api", "apps/web"], []);

        var undone = ScopeRules.ToUndo("org/mono", ["apps/api/a.cs", "apps/web/b.ts", "libs/c.ts"], scope);

        Assert.Equal(["libs/c.ts"], undone.Select(change => change.Path));
    }
}

/// <summary>Which issue a PR belongs to, also when the issue is in another repo.</summary>
public class IssueLinksTests
{
    private const string CrLf = "\r\n";

    private static readonly ProjectConfig Project = TestProjects.SeparateIssuesRepo();
    private const string Issues = "org/mbh-issues";
    private const string Backend = "org/mbh-backend";

    [Theory]
    [InlineData("Adds export.\n\nIssue: org/mbh-issues#12", Issues, 12)]
    [InlineData("Issue: org/mbh-issues#1\nIssue: org/mbh-issues#2", Issues, 2)]    // the runner writes it last
    [InlineData("See org/mbh-issues#7 for the plan.", Issues, 7)]
    [InlineData("Closes #5", Backend, 5)]                                          // same repo as the PR
    [InlineData("Some text about #9.", Backend, 9)]
    [InlineData("Issue: ORG/MBH-Issues#4", Issues, 4)]                              // the project's spelling comes back
    [InlineData("Issue: org/mbh-issues#3 and Closes #5", Issues, 3)]               // the explicit line goes first
    public void The_link_is_found_in_the_order_of_the_spec(string body, string expectedRepo, int expectedNumber)
    {
        var linked = IssueLinks.Find(body, Backend, Project);

        Assert.Equal(new IssueRef(expectedRepo, expectedNumber), linked);
    }

    [Fact]
    public void The_issue_line_is_found_in_a_body_with_windows_line_endings()
    {
        // The runner writes its bodies with AppendLine, which is CRLF on Windows. The final PR lists sub-issues
        // before the Issue line, so a missed line would link the first sub-issue instead of the parent.
        var body = "- #30: Task (sub-issue org/mbh-issues#13)" + CrLf + CrLf + "Issue: org/mbh-issues#12" + CrLf;

        Assert.Equal(new IssueRef(Issues, 12), IssueLinks.Find(body, Backend, Project));
    }

    [Fact]
    public void A_link_to_a_repo_outside_the_project_is_ignored()
    {
        Assert.Null(IssueLinks.Find("Issue: other/repo#3", Backend, Project));
        Assert.Equal(new IssueRef(Backend, 5), IssueLinks.Find("Issue: other/repo#3\nCloses #5", Backend, Project));
    }

    [Fact]
    public void A_url_is_not_a_link()
    {
        Assert.Null(IssueLinks.Find("see https://example.com/foo/bar#12", Backend, Project));
    }

    [Fact]
    public void No_body_means_no_link()
    {
        Assert.Null(IssueLinks.Find(null, Backend, Project));
        Assert.Null(IssueLinks.Find("nothing here", Backend, Project));
    }
}

/// <summary>A simple issue whose PR is open is watched until the PR is merged.</summary>
public class FinishIssuePlanningTests
{
    private static readonly ProjectConfig Project = TestProjects.Single();

    private static PlanDecision Plan(ProjectConfig project, params string[] labels)
    {
        var input = new IssuePlanInput(project, TestProjects.Repo, Samples.Issue(7, labels), [], 0);

        return JobPlanner.PlanIssue(input);
    }

    [Fact]
    public void An_implementing_issue_without_ai_implement_is_checked_on_every_poll()
    {
        var decision = Plan(Project, "ai", Labels.Ready, Labels.Simple, Labels.Implementing);

        Assert.Equal(JobType.FinishIssue, decision.Job?.Type);
        Assert.True(decision.Routine);
    }

    [Fact]
    public void While_ai_implement_is_still_on_the_implement_job_owns_the_issue()
    {
        var decision = Plan(Project, "ai", Labels.Ready, Labels.Simple, Labels.Implement, Labels.Implementing);

        Assert.Null(decision.Job);
        Assert.Contains("already implementing", decision.Reason);
    }

    [Fact]
    public void A_sub_issue_is_closed_by_its_parent_and_not_by_this_job()
    {
        var decision = Plan(Project, "ai", Labels.SubIssue, Labels.Simple, Labels.Implementing);

        Assert.NotEqual(JobType.FinishIssue, decision.Job?.Type);
    }

    [Fact]
    public void A_parent_issue_is_sequenced()
    {
        var decision = Plan(Project, "ai", Labels.Parent, Labels.Implementing);

        Assert.Equal(JobType.SequenceSubIssues, decision.Job?.Type);
    }

    [Fact]
    public void A_failed_issue_waits_for_a_human()
    {
        var decision = Plan(Project, "ai", Labels.Implementing, Labels.Failed);

        Assert.Null(decision.Job);
    }

    [Fact]
    public void Nothing_is_planned_when_the_implement_workflow_is_off()
    {
        var project = TestProjects.Single(workflows: [Workflows.Analyze, Workflows.Review]);

        var decision = Plan(project, "ai", Labels.Implementing);

        Assert.NotEqual(JobType.FinishIssue, decision.Job?.Type);
    }
}
