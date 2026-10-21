using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>What <see cref="IssueStaging.Build"/> needs to stage one issue and its sub-issues.</summary>
/// <param name="SubIssues">The issue's sub-issues in build order (empty for any other issue).</param>
/// <param name="PullsByRepo">Open and closed PRs of the project's code repos, by repo name (case-insensitive).</param>
/// <param name="ActivityOf">The runner's jobs on an issue (this runner's queue and running job), per issue.</param>
public sealed record StagingInput(
    ProjectConfig Project,
    IssueSnapshot Issue,
    IReadOnlyList<IssueSnapshot> SubIssues,
    IReadOnlyDictionary<string, IReadOnlyList<HostPull>> PullsByRepo,
    Func<IssueSnapshot, JobActivity> ActivityOf);

/// <summary>An issue's stage, what it waits on and the input of its actions.</summary>
public sealed record StagedIssue(StageResult Stage, CodeLabel? Waiting, IssueActionInput Actions);

/// <summary>
/// The one place that turns an issue, its sub-issues, the PRs and the job activity into a stage, a waiting-on action
/// and the input of <see cref="IssueActions"/>. The dashboard's list and the check right before an action is written
/// both call it, so "offered" cannot mean two things.
/// </summary>
public static class IssueStaging
{
    public static StagedIssue Build(StagingInput input)
    {
        var (project, issue, subIssues, pullsByRepo, activityOf) = input;

        var progress = subIssues
            .Select(subIssue => ProgressOf(input, subIssue))
            .ToList();
        var stageInput = new StageInput(project, issue, subIssues, pullsByRepo)
        {
            Jobs = activityOf(issue),
        };
        var stage = IssueStage.Compute(stageInput);
        var waitingInput = new WaitingInput(issue, stage, project.Settings.AutoStartFirstSubIssue, progress);
        var waiting = WaitingOn.Of(waitingInput);

        return new StagedIssue(stage, waiting, new IssueActionInput(waitingInput, project.Settings, subIssues));
    }

    private static SubIssueProgress ProgressOf(StagingInput input, IssueSnapshot subIssue)
    {
        var stageInput = new StageInput(input.Project, subIssue, [], input.PullsByRepo)
        {
            Jobs = input.ActivityOf(subIssue),
        };
        var stage = IssueStage.Compute(stageInput);

        return new SubIssueProgress(subIssue.IsOpen, stage.Stage.Code, stage.Badge is not null);
    }
}
