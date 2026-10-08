namespace AiRunner.Infrastructure;

/// <summary>
/// Who the runner is on GitBucket. Login comes from GET /user at startup; the secret is the
/// API token (or GitBucket:GitPassword when git over HTTP needs a different password).
/// </summary>
public sealed class RunnerIdentity
{
    public string Login { get; set; } = "";
    public string GitSecret { get; set; } = "";
}
