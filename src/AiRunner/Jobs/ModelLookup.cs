using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Jobs;

/// <summary>
/// What the model of a Copilot run depends on besides the project's settings (see <see cref="ModelResolver"/>).
/// </summary>
public sealed record ModelContext(string? IssueOverride, ModelComplexity Complexity)
{
    public static readonly ModelContext None = new(null, ModelComplexity.Unknown);

    /// <summary>
    /// The model for a Copilot run of <paramref name="setting"/> (null = none written) in this context.
    /// </summary>
    public ModelChoice Choose(ModelSetting? setting, bool isFixRound) =>
        ModelResolver.Resolve(new ModelInput(setting, IssueOverride, Complexity, isFixRound));
}

/// <summary>Which issue and workflow to read the model settings of, and the models the job's config allows.</summary>
public sealed record ModelQuery(IssueRef Issue, string Workflow, IReadOnlyCollection<string> AllowedModels);

/// <summary>
/// Reads the issue's model override and complexity from the code host, so a job decides on fresh data. A sub-issue
/// reads its own override first, then its parent's, and takes the complexity label of its parent (the breakdown
/// puts it there, the sub-issue has none). An unreadable issue only means "no override": the job still runs.
/// An override outside <c>allowed_models</c> is ignored: the tag is only text in a comment, so the check of the
/// <c>set-model</c> action alone would not hold it.
/// </summary>
public static class ModelLookup
{
    public static async Task<ModelContext> ReadAsync(
        this ICodeHost codeHost,
        ModelQuery query,
        JobRun run,
        CancellationToken cancellationToken)
    {
        var (issueRef, workflow, allowedModels) = query;

        try
        {
            var (repo, number) = issueRef;
            var issue = await codeHost.GetIssueAsync(repo, number, cancellationToken);
            var ownComments = await codeHost.ListCommentsAsync(repo, number, cancellationToken);

            var parentNumber = ParentOf(issue);
            HostIssue? parent = null;
            IReadOnlyList<HostComment>? parentComments = null;

            if (parentNumber is { } parentValue)
            {
                parent = await codeHost.GetIssueAsync(repo, parentValue, cancellationToken);
                parentComments = await codeHost.ListCommentsAsync(repo, parentValue, cancellationToken);
            }

            var issueOverride = ModelOverride.For(workflow, ownComments, parentComments);
            if (issueOverride is not null && !ModelSetting.IsAllowed(allowedModels, issueOverride))
            {
                run.Info($"model override {issueOverride} of {issueRef} ignored: not in allowed_models");
                issueOverride = null;
            }

            var complexity = ComplexityOf(parent ?? issue);

            return new ModelContext(issueOverride, complexity);
        }
        catch (CodeHostException exception)
        {
            run.Info($"model settings of {issueRef} not readable: {exception.Message}");

            return ModelContext.None;
        }
    }

    private static int? ParentOf(HostIssue issue)
    {
        if (!issue.HasLabel(Labels.SubIssue))
        {
            return null;
        }

        // A broken body only means the parent is unknown: the issue's own override and labels are used.
        return JobSupport.SubIssueInfoOf(issue)?.Parent;
    }

    private static ModelComplexity ComplexityOf(HostIssue issue)
    {
        if (issue.HasLabel(Labels.Complex))
        {
            return ModelComplexity.Complex;
        }

        return issue.HasLabel(Labels.Simple) ? ModelComplexity.Simple : ModelComplexity.Unknown;
    }
}
