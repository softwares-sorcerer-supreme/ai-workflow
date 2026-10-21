namespace AiRunner.Infrastructure;

/// <summary>
/// Who the runner is on the code host: the login of the token's user, from GET /user at startup and again after every
/// token the Setup page accepts (another token may belong to another user). The secrets are in
/// <see cref="RunnerCredentials"/>.
/// </summary>
public sealed class RunnerIdentity
{
    public string Login { get; set; } = "";
}
