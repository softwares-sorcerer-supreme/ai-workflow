using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>
/// Workflow 1b: a human approved the breakdown (ai-breakdown). The runner creates the feature
/// branch and one sub-issue per sub-task, in order. No Copilot run: the analysis already has it.
/// </summary>
public sealed class BreakdownIssueJob(
    RunnerConfig config,
    IGitBucketClient gitBucket,
    IWorkspace workspace,
    IGitClient git,
    IStateStore state) : IJob
{
    public JobType Type => JobType.BreakdownIssue;

    public JobInfo Info => new(Workflows.Breakdown, "Breakdown into sub-issues", TimeSpan.FromMinutes(10));

    public async Task<JobOutcome> RunAsync(JobRequest request, JobRun run, CancellationToken ct)
    {
        var (repo, issue, comments, decision) = await gitBucket.LoadIssueAsync(config, state, request, ct);
        if (decision.Job?.Type != Type)
            return JobOutcome.NotNeeded(decision.Reason);

        if (JobPlanner.WhyNotBreakdown(issue.LabelNames()) is { } why)
        {
            await gitBucket.AddCommentAsync(request.Repo, issue.Number, $"I did not create sub-issues: {why}", ct);
            await gitBucket.RemoveLabelAsync(request.Repo, issue.Number, Labels.Breakdown, ct);
            return new JobOutcome("rejected", why);
        }

        var analysis = JobSupport.LatestBlock(comments, Markers.Analysis)
            ?? throw new JobFailedException("No analysis comment found on this issue.");
        var breakdown = JobSupport.Parse(() => BreakdownParser.Parse(analysis), "The proposed breakdown");
        run.Save("breakdown.txt", string.Join("\n", breakdown.Tasks.Select(t => t.ToString())));

        // Feature branch from the default branch (kept if it already exists, e.g. on a retry).
        var defaultBranch = await gitBucket.DefaultBranchAsync(request.Repo, ct);
        var feature = Branches.Feature(issue.Number);
        var dir = await workspace.PrepareAsync(request.Repo, Branches.Remote(defaultBranch), null, [], ct);
        if (!await git.RefExistsAsync(dir, Branches.Remote(feature), ct))
        {
            await git.PushAsync(dir, Branches.Remote(defaultBranch), feature, ct);
            run.Info($"created branch {feature} from {defaultBranch}");
        }

        // Sub-issues, in order. Existing ones (from an earlier, interrupted run) are reused.
        var existing = await gitBucket.ListOpenIssuesAsync(request.Repo, Labels.SubIssue, null, ct);
        var created = new List<(SubTask Task, GbIssue Issue)>();
        foreach (var task in breakdown.Tasks)
        {
            var title = SubIssueBody.Title(issue.Number, task);
            var sub = existing.FirstOrDefault(i => SubIssueBody.IsFor(i, issue.Number, task.Order))
                      ?? await CreateSubIssueAsync(request.Repo, repo, issue.Number, task, breakdown, title, ct);
            created.Add((task, sub));
            run.Info($"sub-issue #{sub.Number}: {title}");
        }

        await gitBucket.AddCommentAsync(request.Repo, issue.Number, ParentComment(issue.Number, created, repo), ct);
        await gitBucket.ApplyLabelsAsync(request.Repo, issue.Number, [Labels.Parent], [Labels.Breakdown], ct);
        return JobOutcome.Ok($"{created.Count} sub-issues");
    }

    private async Task<GbIssue> CreateSubIssueAsync(string repoName, RepoConfig repo, int parent, SubTask task,
        Breakdown breakdown, string title, CancellationToken ct)
    {
        List<string> labels = [config.ScopeLabel, Labels.SubIssue];
        if (!task.NeedsDeveloper)
        {
            labels.AddRange([Labels.Ready, Labels.Simple]);
            if (task.Order == 1 && repo.AutoStartFirstSubIssue) labels.Add(Labels.Implement);
        }

        var body = SubIssueBody.Build(parent, task, breakdown.Tasks.Count, breakdown.SharedContract);
        var sub = await gitBucket.CreateIssueAsync(repoName, title, body, labels, ct);
        // Some GitBucket versions ignore labels on create; add the missing ones.
        await gitBucket.ApplyLabelsAsync(repoName, sub.Number, labels, [], ct);
        return sub;
    }

    private static string ParentComment(int parent, List<(SubTask Task, GbIssue Issue)> subs, RepoConfig repo)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Breakdown approved. Feature branch: `{Branches.Feature(parent)}`.");
        sb.AppendLine();
        sb.AppendLine("Sub-issues, in the order they are built:");
        foreach (var (task, issue) in subs)
            sb.AppendLine($"{task.Order}. #{issue.Number} - {task.Title}{(task.NeedsDeveloper ? " (a developer builds this one)" : "")}");
        sb.AppendLine();
        sb.AppendLine(repo.AutoStartFirstSubIssue
            ? $"The first sub-issue starts automatically."
            : $"Add `{Labels.Implement}` on the first sub-issue to start.");
        sb.AppendLine($"When a sub-issue PR is merged into `{Branches.Feature(parent)}`, the next one starts. " +
                      "After the last one, I open the final PR to the default branch.");
        sb.AppendLine(SubIssueBody.ListMarker(subs.Select(s => s.Issue.Number)));
        return sb.ToString();
    }
}
