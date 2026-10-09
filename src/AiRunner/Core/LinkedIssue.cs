namespace AiRunner.Core;

/// <summary>The issue a PR links to, or Number null. Analysis and Spec are null when not found.</summary>
public sealed record LinkedIssue(int? Number, string? Analysis, string? Spec);
