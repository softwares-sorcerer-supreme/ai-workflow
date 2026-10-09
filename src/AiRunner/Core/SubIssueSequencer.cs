namespace AiRunner.Core;

public enum SequenceStep
{
    Wait, StartNext, OpenFinalPr
}

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
    /// <param name="subIssues">The sub-issues in build order (from the parent's hidden list).</param>
    /// <param name="pulls">Open and closed PRs of the repo.</param>
    public static SequenceDecision Decide(int parent, IReadOnlyList<GbIssue> subIssues, IReadOnlyList<GbPull> pulls)
    {
        for (var order = 1; order <= subIssues.Count; order++)
        {
            var subIssue = subIssues[order - 1];

            var slot = new SubIssueSlot(parent, order);
            var isDone = SubIssueBody.IsDone(subIssue, slot, pulls);

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

            var previousSubIssue = subIssues[order - 2];
            return new(SequenceStep.StartNext, $"#{previousSubIssue.Number} is done", subIssue, previousSubIssue);
        }

        var feature = Branches.Feature(parent);

        return pulls.Any(pull => pull.Head.Ref == feature)
            ? new(SequenceStep.Wait, "final PR already exists")
            : new(SequenceStep.OpenFinalPr, "all sub-issues are done");
    }
}
