namespace AiRunner.Core;

public enum SequenceStep { Wait, StartNext, OpenFinalPr }

/// <param name="Next">The sub-issue to start (StartNext).</param>
/// <param name="Previous">The sub-issue that was just finished (StartNext).</param>
public sealed record SequenceDecision(SequenceStep Step, string Reason, GbIssue? Next = null, GbIssue? Previous = null);

/// <summary>
/// Workflow 1b sequencing: sub-issues are built one at a time, in order. The first one is
/// started by a human (or by the breakdown when auto-start is on); every later one starts
/// when the one before it is done. After the last one, the final PR is opened once.
/// </summary>
public static class SubIssueSequencer
{
    /// <param name="subs">The sub-issues in build order (from the parent's hidden list).</param>
    /// <param name="pulls">Open and closed PRs of the repo.</param>
    public static SequenceDecision Decide(int parent, IReadOnlyList<GbIssue> subs, IReadOnlyList<GbPull> pulls)
    {
        for (var order = 1; order <= subs.Count; order++)
        {
            var sub = subs[order - 1];
            if (SubIssueBody.IsDone(sub, parent, order, pulls))
                continue;

            if (order == 1)
                return new(SequenceStep.Wait, $"waiting for #{sub.Number} to be started");

            var labels = sub.LabelNames();
            if (labels.Contains(Labels.Implement) || labels.Contains(Labels.Implementing) || labels.Contains(Labels.Failed))
                return new(SequenceStep.Wait, $"#{sub.Number} is in progress");
            if (!labels.Contains(Labels.Simple))
                return new(SequenceStep.Wait, $"#{sub.Number} waits for a developer");

            return new(SequenceStep.StartNext, $"#{subs[order - 2].Number} is done", sub, subs[order - 2]);
        }

        var feature = Branches.Feature(parent);
        return pulls.Any(p => p.Head.Ref == feature)
            ? new(SequenceStep.Wait, "final PR already exists")
            : new(SequenceStep.OpenFinalPr, "all sub-issues are done");
    }
}
