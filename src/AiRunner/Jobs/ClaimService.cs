using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Jobs;

/// <summary>The issue or PR a job works on. GitBucket comments and labels a PR through the issue endpoints, so both look the same.</summary>
/// <param name="Until">The deadline of the job that claims; written into the claim so a crashed runner's claim can be taken over.</param>
public sealed record ClaimTarget(string Repo, int Number)
{
    public DateTimeOffset? Until { get; init; }
}

/// <summary>
/// Claims an issue or PR for this runner so that several runners can share projects (claim_enabled). The label is
/// <c>ai:claimed</c>; the claim itself is one comment, edited in place (rules in <see cref="Claims"/>). The race is
/// settled by reading again after the write: the oldest claim comment wins, and a runner that wrote a newer duplicate
/// withdraws it. Two runners that edit the same free comment in the same second can still both read their own name;
/// the window is one API round trip, and a job is idempotent (it plans again on fresh data), so this is accepted.
/// </summary>
public sealed class ClaimService(
    RunnerConfig config,
    ICodeHost codeHost,
    TimeProvider timeProvider)
{
    /// <summary>Whether the project of this repo shares its items between runners.</summary>
    public bool IsEnabled(string repo) => config.FindProject(repo)?.Settings.ClaimEnabled == true;

    /// <summary>True when this runner holds the claim now and may run the job.</summary>
    public async Task<bool> TryClaimAsync(ClaimTarget target, CancellationToken cancellationToken)
    {
        var (repo, number) = target;
        var runnerName = config.EffectiveRunnerName();
        var now = timeProvider.GetUtcNow();

        var comments = await codeHost.ListCommentsAsync(repo, number, cancellationToken);
        var decision = Claims.Decide(comments, runnerName, now);
        if (decision.Action == ClaimAction.Skip)
        {
            return false;
        }

        var text = Claims.Text(runnerName, ClaimState.Claimed, new ClaimWindow(now, target.Until));
        long writtenId;
        if (decision.Action == ClaimAction.Create)
        {
            var created = await codeHost.AddCommentAsync(repo, number, text, cancellationToken);
            writtenId = created.Id;
        }
        else
        {
            writtenId = decision.Existing!.CommentId;
            await codeHost.UpdateCommentAsync(repo, writtenId, text, cancellationToken);
        }

        var afterWrite = await codeHost.ListCommentsAsync(repo, number, cancellationToken);
        if (Claims.IsHeldBy(afterWrite, runnerName))
        {
            await codeHost.AddLabelsAsync(repo, number, [Labels.Claimed], cancellationToken);

            return true;
        }

        // Lost. A duplicate comment made by this runner is withdrawn (never deleted), so the item keeps one live claim.
        var oldest = Claims.All(afterWrite).FirstOrDefault();
        if (decision.Action == ClaimAction.Create && oldest is not null && oldest.CommentId != writtenId)
        {
            var withdrawn = Claims.Text(runnerName, ClaimState.Withdrawn, now);
            await codeHost.UpdateCommentAsync(repo, writtenId, withdrawn, cancellationToken);
        }

        return false;
    }

    /// <summary>Removes the label and edits this runner's claim comment to "released". Safe to call when nothing is held.</summary>
    public async Task ReleaseAsync(ClaimTarget target, CancellationToken cancellationToken)
    {
        var (repo, number) = target;
        var runnerName = config.EffectiveRunnerName();

        var comments = await codeHost.ListCommentsAsync(repo, number, cancellationToken);
        var held = Claims.HeldComment(comments, runnerName);
        if (held is not null)
        {
            var released = Claims.Text(runnerName, ClaimState.Released, timeProvider.GetUtcNow());
            await codeHost.UpdateCommentAsync(repo, held.CommentId, released, cancellationToken);
        }

        await codeHost.RemoveLabelAsync(repo, number, Labels.Claimed, cancellationToken);
    }
}
