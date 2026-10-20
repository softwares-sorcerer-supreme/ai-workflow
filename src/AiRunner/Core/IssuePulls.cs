namespace AiRunner.Core;

/// <summary>Where the PRs of one piece of work stand: a merged one wins over an open one.</summary>
public enum PullOutcome
{
    None, Open, Merged, ClosedWithoutMerge
}

/// <summary>
/// "Its PR": the PRs the runner opened for one issue, found by their branch. The jobs that close issues after a merge
/// and the dashboard's stage read them through here, so they always agree.
/// </summary>
public static class IssuePulls
{
    /// <summary>The PRs of a simple issue: head ai/issue-{n}, in any base.</summary>
    public static IReadOnlyList<GbPull> OfIssue(int number, IEnumerable<GbPull> pulls)
    {
        var branch = Branches.Issue(number);

        return pulls
            .Where(pull => pull.Head.Ref == branch)
            .ToList();
    }

    /// <summary>
    /// Merged when any PR is merged, else open when any is still open, else closed without a merge when there is
    /// at least one, else none.
    /// </summary>
    public static PullOutcome OutcomeOf(IReadOnlyCollection<GbPull> pulls)
    {
        if (pulls.Any(pull => pull.IsMerged()))
        {
            return PullOutcome.Merged;
        }

        if (pulls.Any(pull => pull.IsOpen()))
        {
            return PullOutcome.Open;
        }

        return pulls.Count == 0 ? PullOutcome.None : PullOutcome.ClosedWithoutMerge;
    }
}
