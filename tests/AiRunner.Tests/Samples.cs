namespace AiRunner.Tests;

/// <summary>Filled templates, as an agent would write them, and small GitBucket objects for Core tests.</summary>
public static class Samples
{
    public static HostIssue Issue(int number, params string[] labels)
    {
        var issueLabels = labels.Select(label => new HostLabel(label)).ToList();
        return new(
            number,
            $"Issue {number}",
            null,
            new HostUser("alice"),
            issueLabels,
            DateTimeOffset.UtcNow,
            null,
            "open");
    }

    public static HostComment Human(long id, string body) =>
        new(id, body, new HostUser("alice"), DateTimeOffset.UtcNow);

    public static HostComment Bot(long id, string body) =>
        new(id, BotComments.Marker + "\n" + body, new HostUser("bot"), DateTimeOffset.UtcNow);

    /// <summary>The component of the one-component project of <c>JobTestKit</c>.</summary>
    public const string DefaultComponent = "demo";

    /// <summary>
    /// The Questions section of an open analysis: three groups, one numbering, empty groups say None.
    /// </summary>
    public const string GroupedQuestions = """
        Behaviour (what the app does: flows, rules, roles, screens, data the user sees):
        - Q1: Which columns? (why I ask: the report has 40 columns)
        Technical (how we build it: library, pattern, data access, API shape, performance):
        - None
        Other (scope, timing, who decides, environments):
        - None
        """;

    /// <param name="components">The "Components affected" value. Default: the kit's component, or unknown while questions are open.</param>
    public static string Analysis(
        string status,
        string complexity,
        string breakdown = "None",
        string? components = null) => $"""
        ## AI analysis - round 1

        **Status:** {status}
        **Complexity:** {complexity}
        **Components affected:** {components ?? (status == "ready" ? DefaultComponent : "unknown")}

        ### What I understand
        The user wants to export the report as CSV.

        ### Acceptance criteria (as I understand them)
        1. Given a report, when I click Export, then a CSV file downloads.

        ### Assumptions - please confirm
        - A1: Only CSV. (because: the issue says CSV)

        ### Questions
        {(status == "ready" ? "None" : GroupedQuestions)}

        ### Related code
        - `src/Reports/ReportService.cs` - builds the report

        ### Proposed breakdown
        {breakdown}

        ### Risks
        - Large reports -> stream the file

        _Answer by number (Q1: ..., A2: wrong, it is ...), then write the trigger keyword (@ai by default) in your comment._
        """;

    /// <summary>
    /// The three-row breakdown of the tests; each row names the component given for it. Row 1 builds step 1 of
    /// <see cref="SpecFor"/>, so the SPEC's first component must be the first row's.
    /// </summary>
    public static string Breakdown(string first, string second, string third) => $$"""
        | # | Component | Title | Scope | Touches | Steps | Depends on |
        | --- | --- | --- | --- | --- | --- | --- |
        | 1 | {{first}} | Export entity | Add the Export entity and its table. | src/Domain/Export.cs | 1 | - |
        | 2 | {{second}} | Export command | Add the command that creates an export. | src/Application/Exports | - | 1 |
        | 3 | {{third}} | [developer] Export API | Add POST /api/exports. | src/Api/ExportsController.cs | - | 1, 2 |

        Shared contract: entity Export(Id, RequestedBy, Status); enum ExportStatus { Pending, Done }; POST /api/exports
        """;

    /// <summary>The breakdown with every row in the kit's component.</summary>
    public static readonly string BreakdownTable = Breakdown(DefaultComponent, DefaultComponent, DefaultComponent);

    /// <summary>The API changes table of a SPEC, with one endpoint.</summary>
    public const string ApiTable = """
        | Method | Path | Query / paging | Request | Response | Errors | Auth |
        | --- | --- | --- | --- | --- | --- | --- |
        | GET | /api/exports | page, pageSize | - | PaginationResponse<ExportRow> | 401, 403 | Exports.Read |
        """;

    /// <summary>The UI changes table of a SPEC, with one page.</summary>
    public const string UiTable = """
        | Route | Page / component | Copy from | Shared components | States |
        | --- | --- | --- | --- | --- |
        | /exports | src/Pages/ExportList.cs | src/Pages/ReportList.cs | layout, panel | loading, empty, error, data |
        """;

    /// <summary>A SPEC whose technical plan has one step per component given; API and UI changes are None.</summary>
    public static string SpecFor(params string[] components) => SpecWith(components, "None", "None");

    /// <summary>A SPEC with the given API changes and UI changes sections (a table or None).</summary>
    public static string SpecWith(string[] components, string apiChanges, string uiChanges)
    {
        var rows = components.Select((component, index) =>
            $"| {index + 1} | {component} | `src/Export{index + 1}.cs` | `src/Report{index + 1}.cs` | " +
            "Add the export code. |");
        var table = string.Join("\n", rows);

        return $"""
            ## SPEC - issue #12: Export CSV

            ### Goal
            Export a report as CSV.

            ### Technical plan
            | Step | Component | File | Pattern from | Change |
            | --- | --- | --- | --- | --- |
            {table}

            ### Data changes
            None

            ### API changes
            {apiChanges}

            ### UI changes
            {uiChanges}

            ### Shared contract (exact names)
            - Endpoints: GET /api/exports
            """;
    }

    public static readonly string Spec = SpecFor(DefaultComponent);

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

    /// <param name="previous">Body of `### Previous findings`: None on a full review, a table on an incremental one.</param>
    public static string Review(
        string verdict = "needs changes",
        string severity = "bug",
        string previous = "None") => $"""
        ## AI review - commit abc1234

        **Verdict:** {verdict}

        ### Summary
        Adds CSV export.

        ### Findings
        | # | Severity | Where | Problem | Suggested fix |
        | --- | --- | --- | --- | --- |
        | 1 | {severity} | `a.cs:4` | Null check missing | Add a guard |
        | 2 | style | `a.cs:9` | Long method | Split it |

        ### Previous findings
        {previous}

        ### Spec check
        None

        ### Tests
        - Covered: export
        - Not covered: empty report

        ### Questions for the author
        None
        """;
}
