using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>One sub-issue to create from a task of the breakdown.</summary>
public sealed record SubIssueRequest(RepoConfig Repo, int Parent, SubTask Task, Breakdown Breakdown, string Title);

/// <summary>
/// Workflow 1b: a human approved the breakdown (ai-breakdown). The runner creates the feature
/// branch and one sub-issue per sub-task, in order. No Copilot run: the analysis already has it.
/// </summary>
public sealed class BreakdownIssueJob(
    RunnerConfig config,
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
        var (repo, issue, comments, decision) = await jobLoader.LoadIssueAsync(request, cancellationToken);
        if (decision.Job?.Type != Type)
        {
            return JobOutcome.NotNeeded(decision.Reason);
        }

        var labelNames = issue.LabelNames();
        if (JobPlanner.WhyNotBreakdown(labelNames) is { } rejectionReason)
        {
            await gitBucketClient.AddCommentAsync(
                request.Repo, issue.Number, $"I did not create sub-issues: {rejectionReason}", cancellationToken);
            await gitBucketClient.RemoveLabelAsync(request.Repo, issue.Number, Labels.Breakdown, cancellationToken);
            return new JobOutcome("rejected", rejectionReason);
        }

        var analysis = JobSupport.LatestBlock(comments, Markers.Analysis)
            ?? throw new JobFailedException("No analysis comment found on this issue.");
        var breakdown = JobSupport.Parse(() => BreakdownParser.Parse(analysis), "The proposed breakdown");
        var taskLines = breakdown.Tasks.Select(task => task.ToString());
        var breakdownText = string.Join("\n", taskLines);
        run.Save("breakdown.txt", breakdownText);

        // Feature branch from the default branch (kept if it already exists, e.g. on a retry).
        var defaultBranch = await gitBucketClient.DefaultBranchAsync(request.Repo, cancellationToken);
        var feature = Branches.Feature(issue.Number);
        var defaultRemote = Branches.Remote(defaultBranch);
        var workspaceRequest = new WorkspaceRequest(
            Repo: request.Repo,
            StartPoint: defaultRemote,
            NewBranch: null,
            ExtraFetch: []);
        var directory = await workspace.PrepareAsync(workspaceRequest, cancellationToken);
        var featureRemote = Branches.Remote(feature);
        var featureExists = await git.RefExistsAsync(directory, featureRemote, cancellationToken);
        if (!featureExists)
        {
            await git.PushAsync(directory, defaultRemote, feature, cancellationToken);
            run.Info($"created branch {feature} from {defaultBranch}");
        }

        // Sub-issues, in order. Existing ones (from an earlier, interrupted run) are reused.
        var existing = await gitBucketClient.ListOpenIssuesAsync(
            request.Repo, Labels.SubIssue, null, cancellationToken);
        var created = new List<(SubTask Task, GbIssue Issue)>();
        foreach (var task in breakdown.Tasks)
        {
            var title = SubIssueBody.Title(issue.Number, task);
            var subIssue = existing.FirstOrDefault(
                candidate => SubIssueBody.IsFor(candidate, issue.Number, task.Order));

            // Only a missing sub-issue is created: an existing one comes from an earlier, interrupted run.
            if (subIssue is null)
            {
                var subIssueRequest = new SubIssueRequest(repo, issue.Number, task, breakdown, title);
                subIssue = await CreateSubIssueAsync(subIssueRequest, cancellationToken);
            }

            created.Add((task, subIssue));
            run.Info($"sub-issue #{subIssue.Number}: {title}");
        }

        var parentComment = ParentComment(issue.Number, created, repo);
        await gitBucketClient.AddCommentAsync(request.Repo, issue.Number, parentComment, cancellationToken);

        var labelChange = new LabelChange(Add: [Labels.Parent], Remove: [Labels.Breakdown]);
        await gitBucketClient.ApplyLabelsAsync(request.Repo, issue.Number, labelChange, cancellationToken);

        return JobOutcome.Ok($"{created.Count} sub-issues");
    }

    private async Task<GbIssue> CreateSubIssueAsync(SubIssueRequest request, CancellationToken cancellationToken)
    {
        var (repo, parent, task, breakdown, title) = request;
        List<string> labels = [config.ScopeLabel, Labels.SubIssue];
        if (!task.NeedsDeveloper)
        {
            labels.AddRange([Labels.Ready, Labels.Simple]);
            if (task.Order == 1 && repo.AutoStartFirstSubIssue)
            {
                labels.Add(Labels.Implement);
            }
        }

        var draft = new SubIssueDraft(parent, task, breakdown.Tasks.Count, breakdown.SharedContract);
        var body = SubIssueBody.Build(draft);
        var newIssue = new NewIssue(title, body, labels);
        var subIssue = await gitBucketClient.CreateIssueAsync(repo.FullName, newIssue, cancellationToken);

        // Some GitBucket versions ignore labels on create; add the missing ones.
        var labelChange = new LabelChange(Add: labels, Remove: []);
        await gitBucketClient.ApplyLabelsAsync(repo.FullName, subIssue.Number, labelChange, cancellationToken);

        return subIssue;
    }

    private static string ParentComment(int parent, List<(SubTask Task, GbIssue Issue)> subIssues, RepoConfig repo)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Breakdown approved. Feature branch: `{Branches.Feature(parent)}`.");
        builder.AppendLine();
        builder.AppendLine("Sub-issues, in the order they are built:");
        foreach (var (task, issue) in subIssues)
        {
            var developerNote = task.NeedsDeveloper ? " (a developer builds this one)" : "";
            builder.AppendLine($"{task.Order}. #{issue.Number} - {task.Title}{developerNote}");
        }

        builder.AppendLine();
        builder.AppendLine(repo.AutoStartFirstSubIssue
            ? $"The first sub-issue starts automatically."
            : $"Add `{Labels.Implement}` on the first sub-issue to start.");
        builder.AppendLine($"When a sub-issue PR is merged into `{Branches.Feature(parent)}`, the next one starts. " +
                           "After the last one, I open the final PR to the default branch.");
        var issueNumbers = subIssues.Select(subIssue => subIssue.Issue.Number);
        var listMarker = SubIssueBody.ListMarker(issueNumbers);
        builder.AppendLine(listMarker);

        return builder.ToString();
    }
}
