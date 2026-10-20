using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>
/// What the runner knows of the AI reviews of one PR: the head of the last review (the base of the next incremental
/// one) and how many automatic reviews ran. Kept in review_state and, so ai-runner.db stays safe to delete, in a tag
/// the runner writes after the REVIEW block (the agent cannot fake it: Markers.Wrap neutralises "&lt;!--" in a
/// block).
/// </summary>
/// <param name="ReviewedSha">Head commit of the last posted review, or null when there was none.</param>
/// <param name="Count">Automatic reviews so far; a full review asked for by label is not counted.</param>
public sealed record ReviewHistory(string? ReviewedSha, int Count)
{
    private static readonly ReviewHistory None = new(null, 0);

    private const string TagPrefix = "<!-- ai-runner:review ";

    private static readonly Regex TagPattern = new(Regex.Escape(TagPrefix) + @"sha=(?<sha>[^\s>]+) n=(?<n>\d+) -->");

    /// <summary>The tag that goes after the REVIEW block of the comment.</summary>
    public string Tag() => $"{TagPrefix}sha={ReviewedSha} n={Count} -->";

    /// <summary>
    /// The local state when it has a count, else the tag of the newest REVIEW comment (the database was deleted),
    /// else no history.
    /// </summary>
    public static ReviewHistory Of(ReviewHistory? stored, string? reviewCommentBody)
    {
        if (stored is not null)
        {
            return stored;
        }

        var match = reviewCommentBody is null ? null : TagPattern.Matches(reviewCommentBody).LastOrDefault();

        return match is null
            ? None
            : new ReviewHistory(match.Groups["sha"].Value, int.Parse(match.Groups["n"].Value));
    }

    /// <summary>The line under the review that used the last automatic review, or null before that.</summary>
    public static string? LimitNote(int count, int maxReviews) =>
        count < maxReviews
            ? null
            : $"_The {maxReviews} automatic reviews of limits.max_reviews are used: new commits are not reviewed. " +
              $"Add the label `{Labels.ReviewFull}` for one more full review._";
}
