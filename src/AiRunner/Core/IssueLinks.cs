using System.Text.RegularExpressions;
using AiRunner.Configuration;

namespace AiRunner.Core;

/// <summary>An issue in a repo, as a PR body links it.</summary>
public sealed record IssueRef(string Repo, int Number)
{
    public override string ToString() => $"{Repo}#{Number}";
}

/// <summary>Finds the issue a PR belongs to. The issue may be in another repo than the PR (the issues repo).</summary>
public static class IssueLinks
{
    private const string RepoPattern = @"[\w.-]+/[\w.-]+";

    private static readonly Regex IssueLine = new(
        $@"^[ \t]*Issue:[ \t]*(?<repo>{RepoPattern})#(?<n>\d+)[ \t\r]*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex AnyRepoLink = new(
        $@"(?<![\w/.-])(?<repo>{RepoPattern})#(?<n>\d+)\b",
        RegexOptions.IgnoreCase);

    private static readonly Regex WholeRef = new($@"^(?<repo>{RepoPattern})#(?<n>\d+)$");

    /// <summary>Exactly <c>owner/repo#n</c> (n above 0), as a person types it on the command line; else null.</summary>
    public static IssueRef? ParseRef(string text)
    {
        var trimmed = text.Trim();
        var match = WholeRef.Match(trimmed);
        if (!match.Success || !int.TryParse(match.Groups["n"].Value, out var number) || number <= 0)
        {
            return null;
        }

        return new IssueRef(match.Groups["repo"].Value, number);
    }

    /// <summary>
    /// In this order: an <c>Issue: owner/repo#n</c> line (the last one: the runner writes it last); any
    /// <c>owner/repo#n</c>; <c>close/fix/resolve #n</c>; the first <c>#n</c> (both in the repo of the PR).
    /// A link to a repo outside the project is ignored, so a PR text cannot make the runner read other repos.
    /// </summary>
    public static IssueRef? Find(string? prBody, string pullRepo, ProjectConfig project)
    {
        if (prBody is null)
        {
            return null;
        }

        var issueLines = IssueLine.Matches(prBody);
        if (issueLines.Count > 0)
        {
            var last = issueLines[^1];
            if (InProject(project, last.Groups["repo"].Value) is { } repo)
            {
                return new IssueRef(repo, int.Parse(last.Groups["n"].Value));
            }
        }

        foreach (Match match in AnyRepoLink.Matches(prBody))
        {
            if (InProject(project, match.Groups["repo"].Value) is { } repo)
            {
                return new IssueRef(repo, int.Parse(match.Groups["n"].Value));
            }
        }

        return ContextBuilder.LinkedIssue(prBody) is { } number ? new IssueRef(pullRepo, number) : null;
    }

    /// <summary>The project's spelling of the repo name, or null when the repo is not in the project.</summary>
    private static string? InProject(ProjectConfig project, string repo) =>
        project.Repos.FirstOrDefault(known => string.Equals(known, repo, StringComparison.OrdinalIgnoreCase));
}
