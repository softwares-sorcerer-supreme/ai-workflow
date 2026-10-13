using System.Text;
using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Jobs;

/// <summary>Labels to put on an issue and labels to take off it, in one step.</summary>
public sealed record LabelChange(IEnumerable<string> Add, IEnumerable<string> Remove)
{
    public static LabelChange Adding(params string[] labels) => new(labels, []);

    public static LabelChange Removing(params string[] labels) => new([], labels);
}

/// <summary>Small steps several jobs share. No decisions here; those live in Core.</summary>
public static class JobSupport
{
    public static ProjectConfig ProjectOf(this RunnerConfig config, string repo) =>
        config.FindProject(repo) ?? throw new JobFailedException($"{repo} is not in repos.yml");

    /// <summary>
    /// The component a job changes. Work items name their component; data written before components existed
    /// does not, and then only a project with exactly one code component leaves no doubt.
    /// </summary>
    public static ComponentConfig TargetComponent(this ProjectConfig project, string? componentName = null)
    {
        if (componentName is not null)
        {
            return project.CodeComponents.FirstOrDefault(component => component.Name == componentName)
                   ?? throw new JobFailedException(
                       $"Component `{componentName}` is not a code component of project `{project.Name}`.");
        }

        return project.SoleCodeComponent
               ?? throw new JobFailedException(
                   $"Project `{project.Name}` has more than one code component and nothing says which one this work is for. " +
                   $"Write {project.Settings.TriggerKeyword} to run the analysis again.");
    }

    /// <summary>
    /// The component a simple issue is built in: the one its analysis names. An analysis from before components
    /// existed names none, and then only a project with exactly one code component leaves no doubt.
    /// </summary>
    public static ComponentConfig TargetOfIssue(this ProjectConfig project, IEnumerable<GbComment> comments)
    {
        var analysis = LatestBlock(comments, Markers.Analysis);
        var codeNames = project.CodeComponentNames;
        var names = analysis is null ? [] : AnalysisParser.ComponentsOf(analysis, codeNames);

        if (names.Count > 1)
        {
            throw new JobFailedException(
                $"The analysis names {names.Count} components ({string.Join(", ", names)}), so this issue is not simple. " +
                $"Write {project.Settings.TriggerKeyword} to run the analysis again, or break it down.");
        }

        return project.TargetComponent(names.Count == 1 ? names[0] : null);
    }

    /// <summary>
    /// The "Where to work" section at the end of plan.md: the writable component(s), their folders and the checks the
    /// runner will run. All writable components are in the target repo, so they share one clone.
    /// </summary>
    public static string WhereToWorkSection(IReadOnlyList<ComponentConfig> writable, PreparedWorkspace prepared)
    {
        var builder = new StringBuilder();
        builder.AppendLine().AppendLine();
        builder.AppendLine("## Where to work");

        if (writable.Count == 1)
        {
            var target = writable[0];
            builder.AppendLine($"- Target component: `{target.Name}` ({target.Repo}, path `{target.Path}`).");
            AppendComponentFolders(builder, target, prepared);
            builder.AppendLine("- Write only inside this folder. Every other component is read-only (see components.md).");

            return builder.ToString();
        }

        builder.AppendLine("- Target components (nothing says which one this work is for, so you may write in each of them):");

        foreach (var component in writable)
        {
            builder.AppendLine($"- `{component.Name}` ({component.Repo}, path `{component.Path}`):");
            AppendComponentFolders(builder, component, prepared);
        }

        builder.AppendLine("- Write only inside these folders. Every other component is read-only (see components.md).");

        return builder.ToString();
    }

    private static void AppendComponentFolders(StringBuilder builder, ComponentConfig component, PreparedWorkspace prepared)
    {
        var folder = ComponentsDoc.FolderOf(prepared.Root, component);
        var commandFolder = component.CommandFolder(prepared.TargetClone);
        builder.AppendLine($"- Folder: {folder}");
        builder.AppendLine($"- Run the checks in: {commandFolder}");

        foreach (var command in component.Commands.Verification())
        {
            builder.AppendLine($"- Check: `{command}`");
        }
    }

    /// <summary>A PR number as a link from the issues repo: plain when the PR is in that repo, else with its repo name.</summary>
    public static string PullRef(int number, string issuesRepo, string pullRepo) =>
        string.Equals(issuesRepo, pullRepo, StringComparison.OrdinalIgnoreCase)
            ? $"#{number}"
            : $"{pullRepo}#{number}";

    /// <summary>
    /// Adds the labels that are missing and removes the ones that are present. Reads the issue
    /// again first: labels change during a job, and a stale copy would skip a removal.
    /// </summary>
    public static async Task ApplyLabelsAsync(
        this IGitBucketClient gitBucketClient,
        string repo,
        int number,
        LabelChange change,
        CancellationToken cancellationToken)
    {
        var issue = await gitBucketClient.GetIssueAsync(repo, number, cancellationToken);
        var current = issue.LabelNames();
        var missing = change.Add.Where(labelName => !current.Contains(labelName)).Distinct();
        await gitBucketClient.AddLabelsAsync(repo, number, missing, cancellationToken);

        foreach (var label in change.Remove.Where(current.Contains).Distinct())
        {
            await gitBucketClient.RemoveLabelAsync(repo, number, label, cancellationToken);
        }
    }

    public static async Task<string> DefaultBranchAsync(
        this IGitBucketClient gitBucketClient, string repo, CancellationToken cancellationToken) =>
        (await gitBucketClient.GetRepoAsync(repo, cancellationToken)).DefaultBranch
        ?? throw new JobFailedException($"GitBucket did not return the default branch of {repo}");

    /// <summary>Open and closed PRs of a repo (merged ones are closed), to look a PR up by its branch.</summary>
    public static async Task<List<GbPull>> ListAllPullsAsync(
        this IGitBucketClient gitBucketClient,
        string repo,
        CancellationToken cancellationToken)
    {
        var open = await gitBucketClient.ListPullsAsync(repo, "open", cancellationToken);
        var closed = await gitBucketClient.ListPullsAsync(repo, "closed", cancellationToken);
        List<GbPull> pulls = [.. open, .. closed];

        return pulls;
    }

    /// <summary>
    /// An issue whose PR is merged: comment why, drop ai:implementing and close it. GitBucket cannot close an issue
    /// of another repo, so the runner does it.
    /// </summary>
    public static async Task CloseDoneAsync(
        this IGitBucketClient gitBucketClient,
        string repo,
        int number,
        string comment,
        CancellationToken cancellationToken)
    {
        await gitBucketClient.AddCommentAsync(repo, number, comment, cancellationToken);
        await gitBucketClient.RemoveLabelAsync(repo, number, Labels.Implementing, cancellationToken);
        await gitBucketClient.CloseIssueAsync(repo, number, cancellationToken);
    }

    /// <summary>
    /// The issue a PR links to ("Issue: owner/repo#12", the line the runner writes) with its final analysis and
    /// SPEC, read from the issue's own repo. A sub-issue has no SPEC comment: its body (scope, touches, shared
    /// contract) is its plan and comes back as the SPEC. The components it names come back too. An unreadable
    /// issue is logged, not fatal: the PR can still be handled without it.
    /// </summary>
    public static async Task<LinkedIssue> LinkedIssueAsync(
        this IGitBucketClient gitBucketClient,
        PullLink link,
        JobRun run,
        CancellationToken cancellationToken)
    {
        var (project, pullRepo, pull) = link;
        var linked = IssueLinks.Find(pull.Body, pullRepo, project);
        if (linked is null)
        {
            return new LinkedIssue(null, null, null);
        }

        var (repo, number) = linked;
        try
        {
            var comments = await gitBucketClient.ListCommentsAsync(repo, number, cancellationToken);
            var analysis = LatestBlock(comments, Markers.Analysis);
            var spec = LatestBlock(comments, Markers.Spec);
            var codeNames = project.CodeComponentNames;
            IReadOnlyList<string> components = analysis is null ? [] : AnalysisParser.ComponentsOf(analysis, codeNames);

            if (spec is null)
            {
                var issue = await gitBucketClient.GetIssueAsync(repo, number, cancellationToken);
                if (issue.HasLabel(Labels.SubIssue))
                {
                    spec = ContextBuilder.PlanFromSubIssue(issue);
                    components = SubIssueComponents(issue);
                }
            }

            return new LinkedIssue(number, analysis, spec)
            {
                Repo = repo,
                Components = components,
            };
        }
        catch (GitBucketException exception)
        {
            run.Info($"linked issue {linked} not readable: {exception.Message}");
            return new LinkedIssue(number, null, null)
            {
                Repo = repo,
            };
        }
    }

    /// <summary>The <c>Component:</c> line of a sub-issue body, as a list of one; empty when there is none.</summary>
    private static IReadOnlyList<string> SubIssueComponents(GbIssue issue)
    {
        try
        {
            var info = SubIssueBody.Parse(issue.Body);

            return info?.Component is { } component ? [component] : [];
        }
        catch (FormatException)
        {
            // A broken body only means the component is unknown here; the plan is still given to the agent.
            return [];
        }
    }

    /// <summary>The block inside the newest bot comment of that kind (ANALYSIS, SPEC, REVIEW), or null.</summary>
    public static string? LatestBlock(IEnumerable<GbComment> comments, string marker) =>
        BotComments.Latest(comments, marker) is { } comment ? Markers.Extract(comment.Body!, marker) : null;

    /// <summary>
    /// Runs Copilot and fails the job with a clear message when it did not finish well. The output is
    /// saved as {logName}-stdout.txt and {logName}-stderr.txt (a job with several runs names each one).
    /// </summary>
    public static async Task<string> RunCopilotAsync(this ICopilotRunner copilot, CopilotRequest request, JobRun run,
        CancellationToken cancellationToken, string logName = "copilot")
    {
        var result = await copilot.RunAsync(request, cancellationToken);
        run.Save($"{logName}-stdout.txt", result.StdOut);
        run.Save($"{logName}-stderr.txt", result.StdErr);
        run.Info($"copilot exit {result.ExitCode} after {result.Duration.TotalSeconds:F0}s");

        if (result.TimedOut)
        {
            throw new JobFailedException($"Copilot did not finish within {request.Timeout.TotalMinutes:F0} minutes.");
        }

        if (!result.Ok && CopilotRunner.LooksLikeQuotaError(result))
        {
            throw new JobFailedException("Copilot stopped: the premium requests of the seat look used up. " +
                                         $"Wait for the quota to reset, then remove {Labels.Failed} to retry.");
        }

        if (!result.Ok)
        {
            throw new JobFailedException($"Copilot exited with code {result.ExitCode}: {Text.Short(result.StdErr)}");
        }

        return result.StdOut;
    }

    /// <summary>The marked block from agent output; a missing block fails the job (never post raw output).</summary>
    public static string RequireBlock(string output, string marker, JobRun run)
    {
        var block = Markers.Extract(output, marker)
            ?? throw new JobFailedException($"No {marker} block in the Copilot output (markers missing).");
        run.Save($"{marker.ToLowerInvariant()}.md", block);
        return block;
    }

    /// <summary>Calls a Core parser; a format error becomes a job failure with the parser's message.</summary>
    public static T Parse<T>(Func<T> parse, string what)
    {
        try
        {
            return parse();
        }
        catch (FormatException exception)
        {
            throw new JobFailedException($"{what} has the wrong format: {exception.Message}");
        }
    }
}

/// <summary>A PR and where it is: the project and the repo (one of its code repos) the PR lives in.</summary>
public sealed record PullLink(ProjectConfig Project, string PullRepo, GbPull Pull);
