using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Web;

/// <summary>A value computed in C#: a stable code for the UI to style plus the text to show.</summary>
public sealed record CodeLabelResponse(string Code, string Label)
{
    public static CodeLabelResponse From(CodeLabel value) => new(value.Code, value.Label);
}

/// <summary>
/// One issue of the snapshot with its stage, flags and badge. <c>SubIssues</c> holds the sub-issues of a parent in
/// the nested list and is empty in the flat list.
/// </summary>
public sealed record IssueRowResponse(
    string Project,
    string Repo,
    int Number,
    string Title,
    string State,
    string StateLabel,
    string Stage,
    string StageLabel,
    IReadOnlyList<CodeLabelResponse> Flags,
    CodeLabelResponse? Badge,
    string? Component,
    string? HtmlUrl,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    int? Parent,
    IReadOnlyList<IssueRowResponse> SubIssues);

/// <summary>A project and its code components: the options of the project and component filters.</summary>
public sealed record ProjectOptionResponse(string Name, IReadOnlyList<string> Components);

/// <summary><c>GET /api/issues</c>: the issue rows, newest first, and the options of the filters.</summary>
public sealed record IssueListResponse(
    IReadOnlyList<IssueRowResponse> Items,
    IReadOnlyList<ProjectOptionResponse> Projects,
    IReadOnlyList<CodeLabelResponse> Stages);

/// <summary>The filters of <c>GET /api/issues</c>, already checked. Null = no filter.</summary>
public sealed record IssueQuery(string? Project, string? Component, string? Stage, string? State, bool IsFlat);

/// <summary>
/// Builds the issue list from the snapshot and the job table. The stage, flags and badge come from
/// <see cref="IssueStage"/>; this class only reads, filters and nests.
/// </summary>
public sealed class IssueReader(RunnerConfig config, ISnapshotReader snapshot, IJobReader jobs)
{
    public IReadOnlyList<ProjectOptionResponse> ProjectOptions() =>
        config.Projects
            .Select(project => new ProjectOptionResponse(project.Name, project.CodeComponentNames))
            .ToList();

    public static IReadOnlyList<CodeLabelResponse> StageOptions() =>
        IssueStage.Stages
            .Select(CodeLabelResponse.From)
            .ToList();

    public IssueListResponse Read(IssueQuery query)
    {
        var issues = snapshot.ListIssues();
        var pullsByRepo = PullsByRepo();
        var activeJobs = jobs.ListActive();

        var rows = new List<IssueRowResponse>();
        foreach (var issue in issues)
        {
            var project = config.Projects.FirstOrDefault(candidate => candidate.Name == issue.Project);

            // A project no longer in repos.yml is not polled any more: its old rows stay in the cache, unshown.
            if (project is null)
            {
                continue;
            }

            var subIssues = issues
                .Where(other => other.Parent == issue.Number && SameRepo(other.Repo, issue.Repo))
                .ToList();
            var input = new StageInput(project, issue, subIssues, pullsByRepo)
            {
                Jobs = ActivityOf(issue, activeJobs),
                RunnerName = config.EffectiveRunnerName(),
            };
            var stage = IssueStage.Compute(input);
            var component = SnapshotRules.ComponentOf(issue, project);

            rows.Add(Row(issue, stage, component));
        }

        var matching = rows
            .Where(row => Matches(row, query))
            .ToList();
        var items = query.IsFlat ? Newest(matching) : Nest(rows, matching);

        return new IssueListResponse(items, ProjectOptions(), StageOptions());
    }

    private Dictionary<string, IReadOnlyList<GbPull>> PullsByRepo() =>
        snapshot.ListPulls()
            .GroupBy(pull => pull.Repo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GbPull>)group.Select(pull => pull.AsPull()).ToList(),
                StringComparer.OrdinalIgnoreCase);

    private static JobActivity ActivityOf(IssueSnapshot issue, IReadOnlyList<JobRecord> activeJobs)
    {
        var jobsOfIssue = activeJobs
            .Where(job => job.Number == issue.Number && SameRepo(job.Repo, issue.Repo))
            .ToList();
        var isRunning = jobsOfIssue.Any(job => job.Status == JobStatus.Running);
        var isQueued = jobsOfIssue.Any(job => job.Status == JobStatus.Pending);

        return new JobActivity(isQueued, isRunning);
    }

    private static IssueRowResponse Row(IssueSnapshot issue, StageResult stage, string? component)
    {
        var flags = stage.Flags
            .Select(CodeLabelResponse.From)
            .ToList();
        var badge = stage.Badge is { } value ? CodeLabelResponse.From(value) : null;
        var stateLabel = issue.IsOpen ? "Open" : "Closed";

        return new IssueRowResponse(
            issue.Project,
            issue.Repo,
            issue.Number,
            issue.Title,
            issue.State,
            stateLabel,
            stage.Stage.Code,
            stage.Stage.Label,
            flags,
            badge,
            component,
            issue.HtmlUrl,
            issue.UpdatedAt,
            issue.ClosedAt,
            issue.Parent,
            []);
    }

    private static bool Matches(IssueRowResponse row, IssueQuery query) =>
        (query.Project is null || row.Project == query.Project) &&
        (query.Component is null || row.Component == query.Component) &&
        (query.Stage is null || row.Stage == query.Stage) &&
        (query.State is null || row.State == query.State);

    private static List<IssueRowResponse> Newest(IEnumerable<IssueRowResponse> rows) =>
        rows
            .OrderByDescending(row => row.Number)
            .ThenBy(row => row.Repo, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Sub-issues go under their parent, in build order. A parent shows when it or one of its sub-issues matches the
    /// filters, with the matching sub-issues only. A sub-issue whose parent is not in the snapshot stands on its own.
    /// </summary>
    private static List<IssueRowResponse> Nest(
        IReadOnlyList<IssueRowResponse> rows,
        IReadOnlyList<IssueRowResponse> matching)
    {
        var top = rows
            .Where(row => row.Parent is null || !rows.Any(other => IsParentOf(other, row)))
            .ToList();

        var items = new List<IssueRowResponse>();
        foreach (var row in top)
        {
            var subIssues = matching
                .Where(subIssue => IsParentOf(row, subIssue))
                .OrderBy(subIssue => subIssue.Number)
                .ToList();

            if (subIssues.Count == 0 && !matching.Contains(row))
            {
                continue;
            }

            items.Add(row with
            {
                SubIssues = subIssues
            });
        }

        return Newest(items);
    }

    private static bool IsParentOf(IssueRowResponse parent, IssueRowResponse subIssue) =>
        subIssue.Parent == parent.Number && SameRepo(parent.Repo, subIssue.Repo);

    private static bool SameRepo(string repo, string other) =>
        string.Equals(repo, other, StringComparison.OrdinalIgnoreCase);
}
