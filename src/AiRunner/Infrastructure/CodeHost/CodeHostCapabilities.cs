namespace AiRunner.Infrastructure.CodeHost;

/// <summary>
/// What a code host does or does not do, named after what a job needs to know, so a job reads a capability instead
/// of assuming GitBucket. Only capabilities a job reads are here; a quirk the provider handles by itself (ASCII only,
/// closing through a web form) stays inside the provider.
/// </summary>
/// <param name="KeepsLabelsOnCreate">
/// The labels sent with a new issue are always set. When false, the job adds them again after the create.
/// </param>
public sealed record CodeHostCapabilities(bool KeepsLabelsOnCreate);
