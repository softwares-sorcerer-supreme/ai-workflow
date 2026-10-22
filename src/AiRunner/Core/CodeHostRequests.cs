namespace AiRunner.Core;

/// <summary>A new issue to create. A host may ignore its labels (CodeHostCapabilities.KeepsLabelsOnCreate).</summary>
public sealed record NewIssue(string Title, string Body, IEnumerable<string> Labels);

/// <summary>A new repo label.</summary>
/// <param name="Color">Hex color without "#".</param>
public sealed record NewLabel(string Name, string Color, string Description);

/// <summary>A new pull request from Head into BaseBranch.</summary>
public sealed record NewPull(string Title, string Head, string BaseBranch, string Body);
