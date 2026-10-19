using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>A stable code for the UI to style plus the text to show.</summary>
public sealed record CodeLabel(string Code, string Label);

/// <summary>Whether this runner has a job for the issue in its queue or running now.</summary>
public sealed record JobActivity(bool IsQueued, bool IsRunning)
{
    public static JobActivity None { get; } = new(false, false);

    /// <summary>This runner's name while its running job holds the claim on the issue, else null.</summary>
    public string? ClaimHolder { get; init; }
}

/// <summary>
/// The codes of the flags and badges next to a stage. The UI keys its colors and icons on them
/// (<c>ISSUE_MARK_TONES</c> in the dashboard's shared/tone.ts), so they never change.
/// </summary>
public static class IssueMarks
{
    public const string Failed = "failed";
    public const string Claimed = "claimed";
    public const string OnGitHub = "on-github";
    public const string Queued = "queued";
    public const string Running = "running";
}

/// <summary>What the stage of one issue is computed from: its row, its sub-issues and the PRs of the project.</summary>
/// <param name="SubIssues">The snapshot rows whose parent is this issue (empty for any other issue).</param>
/// <param name="PullsByRepo">Open and closed PRs of the project's code repos, by repo name (case-insensitive).</param>
public sealed record StageInput(
    ProjectConfig Project,
    IssueSnapshot Issue,
    IReadOnlyList<IssueSnapshot> SubIssues,
    IReadOnlyDictionary<string, IReadOnlyList<GbPull>> PullsByRepo)
{
    public JobActivity Jobs { get; init; } = JobActivity.None;

    public IReadOnlyList<GbPull> PullsOf(string repo) =>
        PullsByRepo.TryGetValue(repo, out var pulls) ? pulls : [];
}

/// <param name="Stage">The workflow stage (codes in <see cref="IssueStage.Stages"/>).</param>
/// <param name="Flags">Failed, Claimed by X, On GitHub: shown next to the stage.</param>
/// <param name="Badge">Queued or Running, when this runner has a job for the issue.</param>
public sealed record StageResult(CodeLabel Stage, IReadOnlyList<CodeLabel> Flags, CodeLabel? Badge);

/// <summary>
/// Where an issue is in the workflow (docs/specs/dashboard.md, Stage): the single source of truth for the dashboard.
/// The rules are checked top to bottom and the first match wins. "Its PR" is found with the same helpers the jobs
/// that close issues after a merge use (<see cref="IssuePulls"/>, <see cref="SubIssueSequencer"/>).
/// </summary>
public static class IssueStage
{
    public const string Done = "done";
    public const string ClosedNoMerge = "closed-no-merge";
    public const string Merged = "merged";
    public const string PrOpen = "pr-open";
    public const string BrokenDown = "broken-down";
    public const string Implementing = "implementing";
    public const string ReadyComplex = "ready-complex";
    public const string ReadySimple = "ready-simple";
    public const string QuestionsOpen = "questions-open";
    public const string New = "new";

    /// <summary>Every stage in the order of the rules, with its plain label (the filter options of the UI).</summary>
    public static IReadOnlyList<CodeLabel> Stages { get; } =
    [
        new(Done, "Done"),
        new(ClosedNoMerge, "Closed (no merge)"),
        new(Merged, "Merged"),
        new(PrOpen, "PR open"),
        new(BrokenDown, "Broken down"),
        new(Implementing, "Implementing"),
        new(ReadyComplex, "Ready - complex"),
        new(ReadySimple, "Ready - simple"),
        new(QuestionsOpen, "Questions open"),
        new(New, "New"),
    ];

    public static StageResult Compute(StageInput input)
    {
        var stage = StageOf(input);
        var flags = FlagsOf(input);
        var badge = BadgeOf(input.Jobs);

        return new StageResult(stage, flags, badge);
    }

    private static CodeLabel StageOf(StageInput input)
    {
        var issue = input.Issue;
        var outcome = PullOutcomeOf(input);

        if (!issue.IsOpen)
        {
            return outcome == PullOutcome.Merged ? Stage(Done) : Stage(ClosedNoMerge);
        }

        if (outcome == PullOutcome.Merged)
        {
            return Stage(Merged);
        }

        if (outcome == PullOutcome.Open)
        {
            return Stage(PrOpen);
        }

        if (issue.HasLabel(Labels.Parent))
        {
            var closedCount = input.SubIssues.Count(subIssue => !subIssue.IsOpen);
            return new CodeLabel(BrokenDown, $"Broken down ({closedCount}/{input.SubIssues.Count})");
        }

        if (issue.HasLabel(Labels.Implementing))
        {
            return Stage(Implementing);
        }

        if (issue.HasLabel(Labels.Ready) && issue.HasLabel(Labels.Complex))
        {
            return Stage(ReadyComplex);
        }

        if (issue.HasLabel(Labels.Ready) && issue.HasLabel(Labels.Simple))
        {
            return Stage(ReadySimple);
        }

        return issue.HasLabel(Labels.QuestionsOpen) ? Stage(QuestionsOpen) : Stage(New);
    }

    /// <summary>
    /// "Its PR": the final PRs for a parent, the slot PR for a sub-issue, the ai/issue-{n} PR for any other issue.
    /// </summary>
    private static PullOutcome PullOutcomeOf(StageInput input)
    {
        var issue = input.Issue;

        if (issue.HasLabel(Labels.Parent) || input.SubIssues.Count > 0)
        {
            return FinalOutcome(input);
        }

        if (issue is { Parent: { } parent, SubOrder: { } order })
        {
            // The PR is in the repo of the sub-issue's component; an unknown component has no PR to find.
            var repo = input.Project.CodeComponentOf(issue.Component)?.Repo;

            if (repo is null)
            {
                return PullOutcome.None;
            }

            var slot = new SubIssueSlot(parent, order);
            var slotPulls = SubIssueSequencer.SlotPulls(slot, input.PullsOf(repo));

            return IssuePulls.OutcomeOf(slotPulls);
        }

        // A simple issue's component is in its analysis comment, which the snapshot does not hold. The branch name
        // carries the issue number, so the PR is looked for in every code repo of the project.
        var projectPulls = input.Project.CodeRepos.SelectMany(input.PullsOf);
        var issuePulls = IssuePulls.OfIssue(issue.Number, projectPulls);

        return IssuePulls.OutcomeOf(issuePulls);
    }

    /// <summary>
    /// The final PRs of a parent, one per repo that has sub-issues: merged when every one is merged, open when any
    /// is open, else none (not opened yet).
    /// </summary>
    private static PullOutcome FinalOutcome(StageInput input)
    {
        var repos = input.SubIssues
            .Select(subIssue => input.Project.CodeComponentOf(subIssue.Component)?.Repo)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var finalPulls = SubIssueSequencer.FinalPulls(input.Issue.Number, repos, input.PullsOf);

        if (SubIssueSequencer.AllMerged(finalPulls))
        {
            return PullOutcome.Merged;
        }

        var isAnyOpen = finalPulls.Values.Any(pull => pull is not null && pull.IsOpen());

        return isAnyOpen ? PullOutcome.Open : PullOutcome.None;
    }

    private static List<CodeLabel> FlagsOf(StageInput input)
    {
        var issue = input.Issue;
        var flags = new List<CodeLabel>();

        if (issue.HasLabel(Labels.Failed))
        {
            flags.Add(new CodeLabel(IssueMarks.Failed, "Failed"));
        }

        // This runner is named only while its job holds the claim. Any other claim, including one this runner left
        // from before a restart (it takes that back on its next job), shows as another runner's.
        if (issue.HasLabel(Labels.Claimed))
        {
            var holder = input.Jobs.ClaimHolder ?? "another runner";
            flags.Add(new CodeLabel(IssueMarks.Claimed, $"Claimed by {holder}"));
        }

        if (issue.HasLabel(Labels.OnGitHub) || issue.HasLabel(Labels.NeedsCloudAgent))
        {
            flags.Add(new CodeLabel(IssueMarks.OnGitHub, "On GitHub"));
        }

        return flags;
    }

    private static CodeLabel? BadgeOf(JobActivity jobs)
    {
        if (jobs.IsRunning)
        {
            return new CodeLabel(IssueMarks.Running, "Running");
        }

        return jobs.IsQueued ? new CodeLabel(IssueMarks.Queued, "Queued") : null;
    }

    private static CodeLabel Stage(string code) => Stages.First(stage => stage.Code == code);
}
