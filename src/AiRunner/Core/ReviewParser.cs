namespace AiRunner.Core;

public sealed record ReviewResult(string Verdict, int Bugs, int Risks, int Style);

/// <summary>
/// Checks a review block (Template H): Verdict and Severity use fixed words so they can be
/// counted across PRs. A value outside the list fails the job.
/// </summary>
public static class ReviewParser
{
    private static readonly string[] Verdicts = ["looks good", "needs changes", "needs discussion"];
    private static readonly string[] Severities = ["bug", "risk", "style"];

    public static ReviewResult Parse(string block)
    {
        var verdict = MarkdownSections.ReadField(block, "Verdict");

        if (!Verdicts.Contains(verdict))
        {
            throw new FormatException($"Verdict must be one of: {string.Join(", ", Verdicts)}; got '{verdict}'");
        }

        var findings = MarkdownSections.Get(block, "Findings")
            ?? throw new FormatException("'### Findings' section not found");

        var counts = Severities.ToDictionary(severityName => severityName, _ => 0);

        foreach (var cells in MarkdownSections.TableRows(findings).Where(row => row.Length >= 2))
        {
            var severity = cells[1].Trim('*', '`', ' ').ToLowerInvariant();

            if (!counts.ContainsKey(severity))
            {
                throw new FormatException($"Severity must be one of: {string.Join(", ", Severities)}; got '{severity}'");
            }

            counts[severity]++;
        }

        return new ReviewResult(verdict, counts["bug"], counts["risk"], counts["style"]);
    }
}
