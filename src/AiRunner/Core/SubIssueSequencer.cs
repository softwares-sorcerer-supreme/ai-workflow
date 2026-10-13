namespace AiRunner.Core;

public enum SequenceStep
{
    Wait, StartNext, OpenFinalPrs, CloseParent
}

/// <param name="Next">The sub-issue to start (StartNext).</param>
/// <param name="Previous">The sub-issue that was just finished (StartNext).</param>
public sealed record SequenceDecision(
    SequenceStep Step,
    string Reason,
    GbIssue? Next = null,
    GbIssue? Previous = null)
{
    /// <summary>
    /// OpenFinalPrs: the repos that still need their final PR (can be empty when only the list comment is missing).
    /// </summary>
    public IReadOnlyList<string> Repos { get; init; } = [];
}

/// <summary>A sub-issue and the repo its PR is made in: the repo of its component.</summary>
public sealed record SubIssueRef(GbIssue Issue, string Repo)
{
    /// <summary>The repos of the sub-issues, each once, in build order.</summary>
    public static IReadOnlyList<string> ReposOf(IEnumerable<SubIssueRef> subIssues) =>
        subIssues
            .Select(subIssue => subIssue.Repo)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>A sub-issue that is still open although its PR is merged.</summary>
public sealed record MergedSubIssue(SubIssueRef SubIssue, GbPull Pull);

/// <summary>A sub-issue marked ai:implementing whose PR was closed without a merge.</summary>
public sealed record AbandonedSubIssue(SubIssueRef SubIssue, GbPull Pull);

/// <summary>
/// What the sequencer needs: the sub-issues in build order and the open and closed PRs of each repo they use.
/// </summary>
/// <param name="PullsByRepo">Open and closed PRs per repo name (case-insensitive).</param>
public sealed record SequenceInput(
    int Parent,
    IReadOnlyList<SubIssueRef> SubIssues,
    IReadOnlyDictionary<string, IReadOnlyList<GbPull>> PullsByRepo)
{
    /// <summary>The parent already has the comment that lists the final PRs.</summary>
    public bool HasFinalList { get; init; }

    /// <summary>The repos that have sub-issues, each once.</summary>
    public IReadOnlyList<string> Repos => SubIssueRef.ReposOf(SubIssues);

    public IReadOnlyList<GbPull> PullsOf(string repo) =>
        PullsByRepo.TryGetValue(repo, out var pulls) ? pulls : [];
}

/// <summary>
/// Workflow 1b sequencing: sub-issues are built one at a time, in order, across components and repos. The first
/// one is started by a human (or by the breakdown when auto-start is on); every later one starts when the one
/// before it is done. After the last one, each repo that has sub-issues gets one final PR; when all of them are
/// merged the parent is closed. Everything is derived from the issues and PRs, so a runner that was off during a
/// merge catches up.
/// </summary>
public static class SubIssueSequencer
{
    /// <summary>
    /// The sub-issues whose PR is merged into the feature branch but that are still open. The job closes them.
    /// </summary>
    public static IReadOnlyList<MergedSubIssue> MergedButOpen(SequenceInput input)
    {
        var merged = new List<MergedSubIssue>();

        for (var order = 1; order <= input.SubIssues.Count; order++)
        {
            var subIssue = input.SubIssues[order - 1];
            if (!subIssue.Issue.IsOpen())
            {
                continue;
            }

            var slot = new SubIssueSlot(input.Parent, order);
            var pull = MergedPull(slot, input.PullsOf(subIssue.Repo));
            if (pull is not null)
            {
                merged.Add(new MergedSubIssue(subIssue, pull));
            }
        }

        return merged;
    }

    /// <summary>
    /// The sub-issues that wait for a PR which is gone: ai:implementing, no merged or open PR on the slot, and a PR
    /// that was closed without a merge. Nothing else would ever move them on.
    /// </summary>
    public static IReadOnlyList<AbandonedSubIssue> ClosedWithoutMerge(SequenceInput input)
    {
        var abandoned = new List<AbandonedSubIssue>();

        for (var order = 1; order <= input.SubIssues.Count; order++)
        {
            var subIssue = input.SubIssues[order - 1];
            if (!subIssue.Issue.IsOpen() || !subIssue.Issue.HasLabel(Labels.Implementing))
            {
                continue;
            }

            var slot = new SubIssueSlot(input.Parent, order);
            var slotPulls = input.PullsOf(subIssue.Repo)
                .Where(pull => pull.Head.Ref == Branches.SubIssue(slot.Parent, slot.Order) &&
                               pull.Base.Ref == Branches.Feature(slot.Parent))
                .ToList();

            if (slotPulls.Count > 0 && !slotPulls.Any(pull => pull.IsMerged() || pull.IsOpen()))
            {
                abandoned.Add(new AbandonedSubIssue(subIssue, slotPulls[0]));
            }
        }

        return abandoned;
    }

    public static SequenceDecision Decide(SequenceInput input)
    {
        var (parent, subIssues, _) = input;

        for (var order = 1; order <= subIssues.Count; order++)
        {
            var subIssue = subIssues[order - 1].Issue;

            var slot = new SubIssueSlot(parent, order);
            var isDone = SubIssueBody.IsDone(subIssue, slot, input.PullsOf(subIssues[order - 1].Repo));

            if (isDone)
            {
                continue;
            }

            if (order == 1)
            {
                return new(SequenceStep.Wait, $"waiting for #{subIssue.Number} to be started");
            }

            var labels = subIssue.LabelNames();

            if (labels.Contains(Labels.Implement) ||
                labels.Contains(Labels.Implementing) ||
                labels.Contains(Labels.Failed))
            {
                return new(SequenceStep.Wait, $"#{subIssue.Number} is in progress");
            }

            if (!labels.Contains(Labels.Simple))
            {
                return new(SequenceStep.Wait, $"#{subIssue.Number} waits for a developer");
            }

            var previousSubIssue = subIssues[order - 2].Issue;
            return new(SequenceStep.StartNext, $"#{previousSubIssue.Number} is done", subIssue, previousSubIssue);
        }

        return DecideFinal(input);
    }

    /// <summary>The final PRs: one per repo that has sub-issues, then the parent is closed when they are all merged.</summary>
    private static SequenceDecision DecideFinal(SequenceInput input)
    {
        var feature = Branches.Feature(input.Parent);
        var repos = input.Repos;

        if (repos.Count == 0)
        {
            return new(SequenceStep.Wait, "no sub-issues");
        }

        var finalPulls = repos.ToDictionary(repo => repo, repo => FinalPull(input.PullsOf(repo), feature));
        var missing = repos.Where(repo => finalPulls[repo] is null).ToList();

        if (missing.Count > 0 || !input.HasFinalList)
        {
            return new(SequenceStep.OpenFinalPrs, "all sub-issues are done") { Repos = missing };
        }

        if (finalPulls.Values.All(pull => pull!.IsMerged()))
        {
            return new(SequenceStep.CloseParent, "all final PRs are merged");
        }

        return new(SequenceStep.Wait, "waiting for the final PRs to be merged");
    }

    /// <summary>The merged PR of a sub-issue slot (its branch into the feature branch), or null.</summary>
    public static GbPull? MergedPull(SubIssueSlot slot, IEnumerable<GbPull> pulls) =>
        pulls.FirstOrDefault(pull =>
            pull.Head.Ref == Branches.SubIssue(slot.Parent, slot.Order) &&
            pull.Base.Ref == Branches.Feature(slot.Parent) &&
            pull.IsMerged());

    /// <summary>
    /// The final PR of a repo: head is the feature branch, merged or still open (a merged one wins). A PR that was
    /// closed without a merge does not count, so the runner opens a new one instead of waiting for it forever.
    /// </summary>
    public static GbPull? FinalPull(IEnumerable<GbPull> pulls, string feature)
    {
        var candidates = pulls.Where(pull => pull.Head.Ref == feature).ToList();

        return candidates.FirstOrDefault(pull => pull.IsMerged())
               ?? candidates.FirstOrDefault(pull => pull.IsOpen());
    }
}
