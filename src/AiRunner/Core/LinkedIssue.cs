namespace AiRunner.Core;

/// <summary>
/// The issue a PR links to, or Number null. Analysis and Spec are null when not found.
/// </summary>
public sealed record LinkedIssue(int? Number, string? Analysis, string? Spec)
{
    /// <summary>The repo of the issue (the issues repo for PRs made by the runner); null when nothing is linked.</summary>
    public string? Repo { get; init; }

    /// <summary>
    /// The code components the issue is for: the <c>Component:</c> line of a sub-issue, else the analysis'
    /// <c>Components affected</c>. Empty when it names none (data from before components existed).
    /// </summary>
    public IReadOnlyList<string> Components { get; init; } = [];

    /// <summary>
    /// The parent issue when the linked issue is a sub-issue. Its <c>Spec</c> is then the sub-issue plan with the SPEC
    /// of that parent, because a sub-issue has no SPEC of its own.
    /// </summary>
    public int? ParentNumber { get; init; }
}
