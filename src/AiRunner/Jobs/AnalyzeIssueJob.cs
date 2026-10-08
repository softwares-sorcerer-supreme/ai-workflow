using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// Workflow 1: Copilot reads the issue thread and the code (read-only) and the runner posts
/// the analysis, sets the status labels and, when ready, posts the SPEC.
/// </summary>
public sealed class AnalyzeIssueJob(
    RunnerConfig config,
    IGitBucketClient gitBucket,
    IWorkspace workspace,
    ICopilotRunner copilot,
    IStateStore state) : IJob
{
    public const string Agent = "grill-issue";
    public const string Prompt =
        "Analyze the issue in context.md. Follow the grill-issue agent rules and fill the templates in .ai-workflow/templates/.";

    public JobType Type => JobType.AnalyzeIssue;

    public JobInfo Info => new(Workflows.Analyze, "Issue analysis", TimeSpan.FromMinutes(config.Copilot.AnalyzeTimeoutMinutes + 10));

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken ct)
    {
        var (repo, issue, comments, decision) = await gitBucket.LoadIssueAsync(config, state, request, ct);
        if (decision.Job?.Type != Type)
            return JobOutcome.NotNeeded(decision.Reason);

        var (round, limitReached) = JobPlanner.NextRound(repo, comments);
        if (limitReached)
            throw new JobFailedException(
                $"{repo.Limits.MaxAnalysisRounds} analysis rounds reached. Please clarify this issue in a call " +
                "and update the issue text; the bot will not ask more questions here.");
        var readUpTo = comments.Count == 0 ? 0 : comments.Max(c => c.Id);

        var defaultBranch = await gitBucket.DefaultBranchAsync(request.Repo, ct);
        var dir = await workspace.PrepareAsync(request.Repo, Branches.Remote(defaultBranch), null, [], ct);
        var context = ContextBuilder.ForIssue(issue, comments, round);
        await File.WriteAllTextAsync(Path.Combine(dir, "context.md"), context, ct);
        run.Save("context.md", context);

        var output = await copilot.RunCopilotAsync(
            new CopilotRequest(dir, Prompt, Agent, repo.ModelFor(Workflows.Analyze),
                TimeSpan.FromMinutes(config.Copilot.AnalyzeTimeoutMinutes))
            {
                DenyTools = ["write", "shell"],
            }, run, ct);

        // Check everything before posting anything, so a bad answer is never half-applied.
        var analysis = JobSupport.RequireBlock(output, Markers.Analysis, run);
        var parsed = JobSupport.Parse(() => AnalysisParser.Parse(analysis), "The analysis");
        var spec = parsed.Status == AnalysisStatus.Ready ? JobSupport.RequireBlock(output, Markers.Spec, run) : null;

        await gitBucket.AddCommentAsync(request.Repo, issue.Number,
            Markers.Wrap(analysis, Markers.Analysis) + "\n" + BotComments.ContextUptoTag(readUpTo), ct);
        await gitBucket.ApplyLabelsAsync(request.Repo, issue.Number, parsed.LabelsToAdd, parsed.LabelsToRemove, ct);
        if (spec is not null)
            await gitBucket.AddCommentAsync(request.Repo, issue.Number, Markers.Wrap(spec, Markers.Spec), ct);

        state.SetLastHandledComment(request.Repo, issue.Number, readUpTo);
        run.Info($"round {round}: {parsed.Status}, {parsed.Complexity}");
        return JobOutcome.Ok($"round {round}, {parsed.Status}, {parsed.Complexity}");
    }
}
