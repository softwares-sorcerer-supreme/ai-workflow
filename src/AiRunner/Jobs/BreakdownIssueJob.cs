using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>One sub-issue to create from a task of the breakdown. It goes into the issues repo, next to its parent.</summary>
public sealed record SubIssueRequest(
    ProjectConfig Project,
    string IssuesRepo,
    int Parent,
    SubTask Task,
    Breakdown Breakdown,
    string Title);

/// <summary>What the comment on the parent issue lists: the sub-issues with their components, and the repos that got a feature branch.</summary>
public sealed record ParentCommentDraft(
    int Parent,
    IReadOnlyList<(SubTask Task, GbIssue Issue)> SubIssues,
    IReadOnlyList<string> FeatureRepos);

/// <summary>
/// Workflow 1b: a human approved the breakdown (ai-breakdown). The runner creates one sub-issue per sub-task,
/// in order, always in the issues repo, and a feature branch in every repo that has at least one sub-issue
/// (components that share a repo share its branch). No Copilot run: the analysis already has it.
/// </summary>
public sealed class BreakdownIssueJob(
    IGitBucketClient gitBucketClient,
    JobLoader jobLoader,
    IWorkspace workspace,
    IGitClient git) : IJob
{
    public JobType Type => JobType.BreakdownIssue;

    public JobInfo Info
    {
        get
        {
            var timeout = TimeSpan.FromMinutes(10);
            return new(Workflows.Breakdown, "Breakdown into sub-issues", timeout);
        }
    }

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken cancellationToken)
    {
        var (project, issue, comments, decision) = await jobLoader.LoadIssueAsync(request, cancellationToken);
        if (decision.Job?.Type != Type)
        {
            return JobOutcome.NotNeeded(decision.Reason);
        }

        var labelNames = issue.LabelNames();
        if (JobPlanner.WhyNotBreakdown(labelNames, project.Settings.TriggerKeyword) is { } rejectionReason)
        {
            await gitBucketClient.AddCommentAsync(
                request.Repo, issue.Number, $"I did not create sub-issues: {rejectionReason}", cancellationToken);
            await gitBucketClient.RemoveLabelAsync(request.Repo, issue.Number, Labels.Breakdown, cancellationToken);
            return new JobOutcome("rejected", rejectionReason, CopilotCalls: 0);
        }

        // A breakdown that cannot be read is a problem of the analysis, not of this job: say so and drop the label,
        // otherwise ai-breakdown would stay and block the @ai comment that asks for a new analysis.
        var analysis = JobSupport.LatestBlock(comments, Markers.Analysis);
        if (analysis is null)
        {
            const string noAnalysis = "no analysis comment found on this issue";

            return await RejectUnreadableAsync(project, issue, noAnalysis, cancellationToken);
        }

        var codeComponents = project.CodeComponentNames;
        Breakdown breakdown;
        try
        {
            breakdown = BreakdownParser.Parse(analysis, codeComponents);
        }
        catch (FormatException exception)
        {
            return await RejectUnreadableAsync(project, issue, exception.Message, cancellationToken);
        }

        var taskLines = breakdown.Tasks.Select(task => task.ToString());
        var breakdownText = string.Join("\n", taskLines);
        run.Save("breakdown.txt", breakdownText);

        // One feature branch per repo that has a sub-issue. The workspace has every repo of the project, and
        // every clone not used as the target sits at its default branch.
        var featureRepos = breakdown.Tasks
            .Select(task => project.TargetComponent(task.Component).Repo)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var firstRepo = featureRepos[0];
        var firstDefaultBranch = await gitBucketClient.DefaultBranchAsync(firstRepo, cancellationToken);
        var workspaceRequest = new WorkspaceRequest(
            Project: project,
            TargetRepo: firstRepo,
            StartPoint: Branches.Remote(firstDefaultBranch),
            NewBranch: null,
            ExtraFetch: []);
        var prepared = await workspace.PrepareAsync(workspaceRequest, cancellationToken);

        foreach (var featureRepo in featureRepos)
        {
            var clone = prepared.CloneOf(featureRepo);
            var feature = Branches.Feature(issue.Number);
            var isNew = await EnsureFeatureBranchAsync(featureRepo, clone, issue.Number, cancellationToken);
            var verb = isNew ? "created" : "found";
            run.Info($"{verb} branch {feature} in {featureRepo}");
        }

        // Sub-issues, in order. Existing ones (from an earlier, interrupted run) are reused, closed ones too: a
        // person may have closed one in the meantime, and a second copy of it would be built twice.
        var open = await gitBucketClient.ListOpenIssuesAsync(
            request.Repo, Labels.SubIssue, null, cancellationToken);
        var closed = await gitBucketClient.ListClosedIssuesAsync(request.Repo, Labels.SubIssue, cancellationToken);
        List<GbIssue> existing = [.. open, .. closed];
        var created = new List<(SubTask Task, GbIssue Issue)>();
        foreach (var task in breakdown.Tasks)
        {
            var title = SubIssueBody.Title(issue.Number, task);
            var subIssue = existing.FirstOrDefault(
                candidate => SubIssueBody.IsFor(candidate, issue.Number, task.Order));

            // Only a missing sub-issue is created: an existing one comes from an earlier, interrupted run.
            if (subIssue is null)
            {
                var subIssueRequest = new SubIssueRequest(
                    project, request.Repo, issue.Number, task, breakdown, title);
                subIssue = await CreateSubIssueAsync(subIssueRequest, cancellationToken);
            }

            created.Add((task, subIssue));
            run.Info($"sub-issue #{subIssue.Number}: {title}");
        }

        var commentDraft = new ParentCommentDraft(issue.Number, created, featureRepos);
        var parentComment = ParentComment(commentDraft, project.Settings);
        await gitBucketClient.AddCommentAsync(request.Repo, issue.Number, parentComment, cancellationToken);

        var labelChange = new LabelChange(Add: [Labels.Parent], Remove: [Labels.Breakdown]);
        await gitBucketClient.ApplyLabelsAsync(request.Repo, issue.Number, labelChange, cancellationToken);

        return JobOutcome.Ok($"{created.Count} sub-issues", copilotCalls: 0);
    }

    private async Task<JobOutcome> RejectUnreadableAsync(
        ProjectConfig project,
        GbIssue issue,
        string problem,
        CancellationToken cancellationToken)
    {
        var reason = $"The proposed breakdown has the wrong format: {problem}. " +
                     $"Write {project.Settings.TriggerKeyword} to ask for a new analysis, " +
                     $"then add `{Labels.Breakdown}` again.";
        await gitBucketClient.AddCommentAsync(
            project.IssuesRepo, issue.Number, $"I did not create sub-issues: {reason}", cancellationToken);
        await gitBucketClient.RemoveLabelAsync(project.IssuesRepo, issue.Number, Labels.Breakdown, cancellationToken);

        return new JobOutcome("rejected", reason, CopilotCalls: 0);
    }

    /// <summary>The feature branch from the default branch of one repo; kept if it exists (a retry, or a second component in the repo).</summary>
    private async Task<bool> EnsureFeatureBranchAsync(
        string repo,
        string clone,
        int parent,
        CancellationToken cancellationToken)
    {
        var defaultBranch = await gitBucketClient.DefaultBranchAsync(repo, cancellationToken);
        var defaultRemote = Branches.Remote(defaultBranch);
        var feature = Branches.Feature(parent);
        var featureRemote = Branches.Remote(feature);

        var featureExists = await git.RefExistsAsync(clone, featureRemote, cancellationToken);
        if (featureExists)
        {
            return false;
        }

        await git.PushAsync(clone, defaultRemote, feature, cancellationToken);

        return true;
    }

    private async Task<GbIssue> CreateSubIssueAsync(SubIssueRequest request, CancellationToken cancellationToken)
    {
        var (project, issuesRepo, parent, task, breakdown, title) = request;
        var settings = project.Settings;
        List<string> labels = [settings.TriggerLabel, Labels.SubIssue];
        if (!task.NeedsDeveloper)
        {
            labels.AddRange([Labels.Ready, Labels.Simple]);
            if (task.Order == 1 && settings.AutoStartFirstSubIssue)
            {
                labels.Add(Labels.Implement);
            }
        }

        var draft = new SubIssueDraft(parent, task, breakdown.Tasks.Count, breakdown.SharedContract);
        var body = SubIssueBody.Build(draft);
        var newIssue = new NewIssue(title, body, labels);
        var subIssue = await gitBucketClient.CreateIssueAsync(issuesRepo, newIssue, cancellationToken);

        // Some GitBucket versions ignore labels on create; add the missing ones.
        var labelChange = new LabelChange(Add: labels, Remove: []);
        await gitBucketClient.ApplyLabelsAsync(issuesRepo, subIssue.Number, labelChange, cancellationToken);

        return subIssue;
    }

    private static string ParentComment(ParentCommentDraft draft, ProjectSettings settings)
    {
        var (parent, subIssues, featureRepos) = draft;
        var builder = new StringBuilder();
        var repoNames = string.Join(", ", featureRepos);
        builder.AppendLine(featureRepos.Count == 1
            ? $"Breakdown approved. Feature branch: `{Branches.Feature(parent)}` in {repoNames}."
            : $"Breakdown approved. Feature branch: `{Branches.Feature(parent)}` in each of: {repoNames}.");
        builder.AppendLine();
        builder.AppendLine("Sub-issues, in the order they are built:");
        foreach (var (task, issue) in subIssues)
        {
            var developerNote = task.NeedsDeveloper ? " (a developer builds this one)" : "";
            builder.AppendLine($"{task.Order}. #{issue.Number} - {task.Title} [{task.Component}]{developerNote}");
        }

        builder.AppendLine();

        // Auto-start never applies to a [developer] row: the first sub-issue then waits for a person.
        var firstTask = subIssues[0].Task;
        if (firstTask.NeedsDeveloper)
        {
            builder.AppendLine("The first sub-issue is built by a developer. Close it when it is done, and the next one starts.");
        }
        else
        {
            builder.AppendLine(settings.AutoStartFirstSubIssue
                ? "The first sub-issue starts automatically."
                : $"Add `{Labels.Implement}` on the first sub-issue to start.");
        }

        builder.AppendLine($"When a sub-issue PR is merged into `{Branches.Feature(parent)}`, the next one starts. " +
                           "After the last one, I open the final PR to the default branch.");
        var issueNumbers = subIssues.Select(subIssue => subIssue.Issue.Number);
        var listMarker = SubIssueBody.ListMarker(issueNumbers);
        builder.AppendLine(listMarker);

        return builder.ToString();
    }
}
