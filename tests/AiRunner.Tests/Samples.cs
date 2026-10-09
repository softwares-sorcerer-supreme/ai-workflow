namespace AiRunner.Tests;

/// <summary>Filled templates, as an agent would write them, and small GitBucket objects for Core tests.</summary>
public static class Samples
{
    public static GbIssue Issue(int number, params string[] labels)
    {
        var issueLabels = labels.Select(label => new GbLabel(label)).ToList();
        return new(
            number,
            $"Issue {number}",
            null,
            new GbUser("alice"),
            issueLabels,
            DateTimeOffset.UtcNow,
            null,
            "open");
    }

    public static GbComment Human(long id, string body) => new(id, body, new GbUser("alice"), DateTimeOffset.UtcNow);

    public static GbComment Bot(long id, string body) =>
        new(id, BotComments.Marker + "\n" + body, new GbUser("bot"), DateTimeOffset.UtcNow);

    public static string Analysis(string status, string complexity, string breakdown = "None") => $"""
        ## AI analysis - round 1

        **Status:** {status}
        **Complexity:** {complexity}

        ### What I understand
        The user wants to export the report as CSV.

        ### Acceptance criteria (as I understand them)
        1. Given a report, when I click Export, then a CSV file downloads.

        ### Assumptions - please confirm
        - A1: Only CSV. (because: the issue says CSV)

        ### Questions
        {(status == "ready" ? "None" : "- Q1: Which columns? (why I ask: the report has 40 columns)")}

        ### Related code
        - `src/Reports/ReportService.cs` - builds the report

        ### Proposed breakdown
        {breakdown}

        ### Risks
        - Large reports -> stream the file

        _Answer by number (Q1: ..., A2: wrong, it is ...), then write @ai in your comment._
        """;

    public const string BreakdownTable = """
        | # | Title | Scope | Touches | Depends on |
        | --- | --- | --- | --- | --- |
        | 1 | Export entity | Add the Export entity and its table. | src/Domain/Export.cs | - |
        | 2 | Export command | Add the command that creates an export. | src/Application/Exports | 1 |
        | 3 | [developer] Export API | Add POST /api/exports. | src/Api/ExportsController.cs | 1, 2 |

        Shared contract: entity Export(Id, RequestedBy, Status); enum ExportStatus { Pending, Done }; POST /api/exports
        """;

    public const string Spec = """
        ## SPEC - issue #12: Export CSV

        ### Goal
        Export a report as CSV.
        """;

    /// <summary>A PR_BODY.md as the implement agent writes it (templates/pr-body.md); complete with the default criteria.</summary>
    public static string PrBody(string criteria = "- [x] 1. Report downloads as CSV - ExportTests.Downloads_csv") => $"""
        ### What changed
        - Adds CSV export. (`src/Export.cs`)

        ### How it was tested
        - `make build` and `make test` pass; added ExportTests.

        ### Acceptance criteria
        {criteria}

        ### Open points
        - None
        """;

    /// <summary>The body of a review fix reply that read the comments up to that id.</summary>
    public static string FixReply(long upTo) => AtAiThread.ReplyBody("## AI fix 1", Markers.ReviewFix, upTo);

    public static string AgentOutput(string analysis, string? spec = null) =>
        "Thinking... reading files\n" +
        Markers.Wrap(analysis, Markers.Analysis) + "\n" +
        (spec is null ? "" : Markers.Wrap(spec, Markers.Spec) + "\n") +
        "Done.\n";

    public static string Review(string verdict = "needs changes", string severity = "bug") => $"""
        ## AI review - commit abc1234

        **Verdict:** {verdict}

        ### Summary
        Adds CSV export.

        ### Findings
        | # | Severity | Where | Problem | Suggested fix |
        | --- | --- | --- | --- | --- |
        | 1 | {severity} | `a.cs:4` | Null check missing | Add a guard |
        | 2 | style | `a.cs:9` | Long method | Split it |

        ### Spec check
        None

        ### Tests
        - Covered: export
        - Not covered: empty report

        ### Questions for the author
        None
        """;
}
