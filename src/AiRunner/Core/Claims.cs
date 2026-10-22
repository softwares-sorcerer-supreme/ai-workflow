using System.Globalization;
using System.Text.RegularExpressions;

namespace AiRunner.Core;

public enum ClaimState
{
    Claimed,
    Released,
    Withdrawn,
}

/// <summary>One claim comment, read back from its hidden tag.</summary>
/// <param name="CommentId">The GitBucket comment that holds the claim.</param>
/// <param name="Runner">The runner name, as written in the tag (see <see cref="Claims.SafeName"/>).</param>
/// <param name="At">When the claim was made; this is the claim time, not the comment time.</param>
public sealed record ClaimInfo(long CommentId, string Runner, ClaimState State, DateTimeOffset At)
{
    /// <summary>When the job that made the claim must be over (its timeout), or null for a claim without one.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>True when the claim was written by the runner with this (already sanitised) name.</summary>
    public bool IsBy(string safeName) => string.Equals(Runner, safeName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The time of a claim and, optionally, the deadline of its job. A plain time converts to a window without a deadline.</summary>
public sealed record ClaimWindow(DateTimeOffset At, DateTimeOffset? Until = null)
{
    public static implicit operator ClaimWindow(DateTimeOffset at) => new(at);
}

public enum ClaimAction
{
    /// <summary>No claim comment yet: write a new one.</summary>
    Create,

    /// <summary>Edit the existing claim comment in place (free, released, mine, or taken over).</summary>
    Edit,

    /// <summary>Another runner holds a fresh claim: do not run.</summary>
    Skip,
}

/// <param name="Existing">The comment to edit (Edit) or the holder (Skip); null for Create.</param>
public sealed record ClaimDecision(ClaimAction Action, ClaimInfo? Existing, string Reason);

/// <summary>
/// Several runners may share projects. A claim is the <c>ai:claimed</c> label plus ONE comment per issue or PR that is
/// edited in place, with a hidden tag that holds the runner name, the state and the time. All rules are here; the
/// GitBucket calls are the claim service's. Comments are never deleted (GitBucket cannot), so a lost race is
/// "withdrawn" by edit.
/// </summary>
public static class Claims
{
    /// <summary>Nobody finishes a job in less than this, so an older claim with no result is a crashed runner.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    private static readonly Regex Tag = new(
        @"<!-- AI_CLAIM runner=(?<runner>\S+) state=(?<state>claimed|released|withdrawn) at=(?<at>\S+)( until=(?<until>\S+))? -->",
        RegexOptions.Compiled);

    /// <summary>Keeps the runner name safe inside the tag: letters, digits, dot, dash and underscore only.</summary>
    public static string SafeName(string runnerName)
    {
        var safe = Regex.Replace(runnerName.Trim(), @"[^A-Za-z0-9._-]", "_");

        return safe.Length == 0 ? "runner" : safe;
    }

    public static string Text(string runnerName, ClaimState state, ClaimWindow window)
    {
        var name = SafeName(runnerName);
        var stamp = window.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var untilTag = window.Until is { } until
            ? " until=" + until.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "";
        var sentence = state switch
        {
            ClaimState.Claimed => $"Claimed by {name}. Other runners skip this item until it is released.",
            ClaimState.Released => $"Released by {name}.",
            _ => $"Withdrawn by {name}: another runner claimed this item first.",
        };

        return $"{BotComments.VisibleTag} {sentence}\n<!-- AI_CLAIM runner={name} state={state.ToString().ToLowerInvariant()} at={stamp}{untilTag} -->";
    }

    /// <summary>The claim in a bot comment, or null when the comment is not a claim.</summary>
    public static ClaimInfo? Read(HostComment comment)
    {
        if (!BotComments.IsBot(comment) || comment.Body is null)
        {
            return null;
        }

        var match = Tag.Match(comment.Body);
        if (!match.Success)
        {
            return null;
        }

        var hasTime = DateTimeOffset.TryParse(
            match.Groups["at"].Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var at);
        if (!hasTime)
        {
            return null;
        }

        var state = Enum.Parse<ClaimState>(match.Groups["state"].Value, ignoreCase: true);

        DateTimeOffset? until = null;
        var untilText = match.Groups["until"];
        if (untilText.Success &&
            DateTimeOffset.TryParse(
                untilText.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedUntil))
        {
            until = parsedUntil;
        }

        return new ClaimInfo(comment.Id, match.Groups["runner"].Value, state, at) { Until = until };
    }

    /// <summary>All claim comments, oldest first. The oldest one is THE claim comment of the item.</summary>
    public static IReadOnlyList<ClaimInfo> All(IEnumerable<HostComment> comments) =>
        comments
            .Select(Read)
            .OfType<ClaimInfo>()
            .OrderBy(claim => claim.CommentId)
            .ToList();

    /// <summary>What a runner does before a job: write, edit, or back off.</summary>
    public static ClaimDecision Decide(IReadOnlyList<HostComment> comments, string runnerName, DateTimeOffset now)
    {
        var claims = All(comments);
        if (claims.Count == 0)
        {
            return new ClaimDecision(ClaimAction.Create, null, "no claim yet");
        }

        var current = claims[0];
        var name = SafeName(runnerName);

        if (current.State != ClaimState.Claimed)
        {
            return new ClaimDecision(ClaimAction.Edit, current, $"the claim was {current.State.ToString().ToLowerInvariant()}");
        }

        if (current.IsBy(name))
        {
            return new ClaimDecision(ClaimAction.Edit, current, "claimed by this runner already");
        }

        if (IsStale(current, comments, now))
        {
            return new ClaimDecision(ClaimAction.Edit, current, $"taking over the stale claim of {current.Runner}");
        }

        return new ClaimDecision(ClaimAction.Skip, current, $"claimed by {current.Runner}");
    }

    /// <summary>
    /// A claim that names the deadline of its job is stale once that deadline (and the minimum of 30 minutes) has
    /// passed: a job is cancelled at its deadline, so the runner is gone. Progress comments such as "Copilot started"
    /// say nothing about that. An older claim without a deadline is stale when it is old and no bot comment follows it.
    /// </summary>
    private static bool IsStale(ClaimInfo claim, IReadOnlyList<HostComment> comments, DateTimeOffset now)
    {
        var earliest = claim.At + StaleAfter;
        if (claim.Until is { } until)
        {
            return now > (until > earliest ? until : earliest);
        }

        var hasResult = comments.Any(
            comment => comment.Id > claim.CommentId && BotComments.IsBot(comment) && Read(comment) is null);

        return now > earliest && !hasResult;
    }

    /// <summary>After writing, the oldest claim comment is the claim. True when it is this runner's and still claimed.</summary>
    public static bool IsHeldBy(IEnumerable<HostComment> comments, string runnerName)
    {
        var claims = All(comments);
        if (claims.Count == 0)
        {
            return false;
        }

        var holder = claims[0];

        return holder.State == ClaimState.Claimed && holder.IsBy(SafeName(runnerName));
    }

    /// <summary>The claim comment of this runner that is still claimed (to release it), or null.</summary>
    public static ClaimInfo? HeldComment(IEnumerable<HostComment> comments, string runnerName)
    {
        var name = SafeName(runnerName);

        return All(comments).FirstOrDefault(claim => claim.State == ClaimState.Claimed && claim.IsBy(name));
    }
}
