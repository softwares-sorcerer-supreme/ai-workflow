namespace AiRunner.Core;

public sealed record ReviewResult(string Verdict, int Bugs, int Risks, int Style)
{
    /// <summary>
    /// Findings where the change differs from its pattern file or its neighbours (naming, layout, error handling,
    /// tests...).
    /// </summary>
    public int Conventions { get; init; }

    /// <summary>Findings of the previous review that this (incremental) review found still open.</summary>
    public int StillOpen { get; init; }
}

/// <summary>
/// Checks a review block (Template H): Verdict and Severity use fixed words so they can be
/// counted across PRs. A value outside the list fails the job.
/// </summary>
public static class ReviewParser
{
    private static readonly string[] Verdicts = ["looks good", "needs changes", "needs discussion"];
    private static readonly string[] Severities = ["bug", "risk", "convention", "style"];
    private static readonly string[] Statuses = ["fixed", "still open"];

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

        var stillOpen = CountStillOpen(block);

        return new ReviewResult(verdict, counts["bug"], counts["risk"], counts["style"])
        {
            Conventions = counts["convention"],
            StillOpen = stillOpen,
        };
    }

    /// <summary>Previous findings an incremental review checked again; a full review writes None.</summary>
    private static int CountStillOpen(string block)
    {
        var previous = MarkdownSections.Get(block, "Previous findings")
            ?? throw new FormatException("'### Previous findings' section not found");

        var stillOpen = 0;
        foreach (var cells in MarkdownSections.TableRows(previous).Where(row => row.Length >= 2))
        {
            var status = cells[1].Trim('*', '`', ' ').ToLowerInvariant();

            if (!Statuses.Contains(status))
            {
                throw new FormatException(
                    $"Previous findings status must be one of: {string.Join(", ", Statuses)}; got '{status}'");
            }

            if (status == "still open")
            {
                stillOpen++;
            }
        }

        return stillOpen;
    }
}
