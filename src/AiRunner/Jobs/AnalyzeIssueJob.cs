using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Jobs;

/// <summary>
/// Workflow 1: Copilot reads the issue thread and the code (read-only) and the runner posts
/// the analysis, sets the status labels and, when ready, posts the SPEC.
/// </summary>
public sealed class AnalyzeIssueJob(
    RunnerConfig config,
    ICodeHost codeHost,
    JobLoader jobLoader,
    IWorkspace workspace,
    ICopilotRunner copilot,
    IStateStore stateStore,
    AttachmentFetcher attachmentFetcher) : IJob
{
    public const string Agent = "grill-issue";
    public const string Prompt =
        JobSupport.RulesPrefix +
        "Analyze the issue in context.md. Read components.md first. " +
        "Follow the grill-issue agent rules and fill the templates in .ai-workflow/templates/.";

    public JobType Type => JobType.AnalyzeIssue;

    public JobInfo Info
    {
        get
        {
            var timeout = TimeSpan.FromMinutes(config.Copilot.AnalyzeTimeoutMinutes + 10);
            return new(Workflows.Analyze, "Issue analysis", timeout);
        }
    }

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken cancellationToken)
    {
        var (project, issue, comments, decision) = await jobLoader.LoadIssueAsync(request, cancellationToken);
        var settings = project.Settings;
        if (decision.Job?.Type != Type)
        {
            return JobOutcome.NotNeeded(decision.Reason);
        }

        // The local cursor only matters for Trigger, which the planner already used.
        var thread = AtAiThread.Of(comments, new ThreadKind(Markers.Analysis, settings.TriggerKeyword), 0);
        var (round, limitReached) = thread.Next(settings.Limits.MaxAnalysisRounds);
        if (limitReached)
        {
            throw new JobFailedException(
                $"{settings.Limits.MaxAnalysisRounds} analysis rounds reached. Please clarify this issue in a call " +
                "and update the issue text; the bot will not ask more questions here.");
        }

        var defaultBranch = await codeHost.DefaultBranchAsync(request.Repo, cancellationToken);
        var startPoint = Branches.Remote(defaultBranch);
        var workspaceRequest = new WorkspaceRequest(
            Project: project,
            TargetRepo: request.Repo,
            StartPoint: startPoint,
            NewBranch: null,
            ExtraFetch: []);
        var prepared = await workspace.PrepareAsync(workspaceRequest, cancellationToken);
        var directory = prepared.Root;

        var issueBody = new AttachmentSource("the issue body", issue.Body);
        var attachmentRequest = AttachmentRequest.Of(directory, issueBody, comments);
        var fetched = await attachmentFetcher.FetchAsync(attachmentRequest, run, cancellationToken);

        var context = ContextBuilder.ForIssue(issue, comments, round) + fetched.Section;
        var contextFile = Path.Combine(directory, "context.md");
        await File.WriteAllTextAsync(contextFile, context, cancellationToken);
        run.Save("context.md", context);

        var model = settings.ModelFor(Workflows.Analyze);
        var timeout = TimeSpan.FromMinutes(config.Copilot.AnalyzeTimeoutMinutes);
        var output = await copilot.RunCopilotAsync(
            new CopilotRequest(directory, Prompt, Agent, model, timeout)
            {
                DenyTools = ["write", "shell"],
                Attachments = fetched.ForCopilot,
            }, run, cancellationToken);

        // Check everything before posting anything, so a bad answer is never half-applied.
        var analysis = JobSupport.RequireBlock(output, Markers.Analysis, run);
        var codeComponents = project.CodeComponentNames;
        var parsed = JobSupport.Parse(() => AnalysisParser.Parse(analysis, codeComponents), "The analysis");
        var spec = parsed.Status == AnalysisStatus.Ready ? JobSupport.RequireBlock(output, Markers.Spec, run) : null;

        // A ready complex issue is next broken down by a human label; a bad table would only show up then.
        if (parsed.Status == AnalysisStatus.Ready && parsed.Complexity == Complexity.Complex)
        {
            JobSupport.Parse(() => BreakdownParser.Parse(analysis, codeComponents), "The proposed breakdown");
        }

        if (spec is not null)
        {
            JobSupport.Parse(
                () =>
                {
                    var steps = SpecParser.Parse(spec, codeComponents);
                    SpecParser.Check(spec);

                    return steps;
                },
                "The SPEC");
        }

        var reply = thread.Reply(analysis);
        await codeHost.AddCommentAsync(request.Repo, issue.Number, reply, cancellationToken);
        var labelChange = new LabelChange(Add: parsed.LabelsToAdd, Remove: parsed.LabelsToRemove);
        await codeHost.ApplyLabelsAsync(request.Repo, issue.Number, labelChange, cancellationToken);
        if (spec is not null)
        {
            var specComment = Markers.Wrap(spec, Markers.Spec);
            await codeHost.AddCommentAsync(request.Repo, issue.Number, specComment, cancellationToken);
        }

        stateStore.SetLastHandledComment(request.Repo, issue.Number, thread.ReadUpTo);
        run.Info($"round {round}: {parsed.Status}, {parsed.Complexity}");

        return JobOutcome.Ok($"round {round}, {parsed.Status}, {parsed.Complexity}", copilotCalls: 1);
    }
}
