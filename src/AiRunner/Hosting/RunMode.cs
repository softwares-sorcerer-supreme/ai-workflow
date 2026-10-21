using AiRunner.Configuration;

namespace AiRunner.Hosting;

/// <summary>
/// The command-line flags of the runner. Kept out of Program.cs so the rule "which mode starts what" is testable.
/// </summary>
public sealed record RunMode
{
    public bool RunOnce { get; init; }
    public bool DryRun { get; init; }
    public bool CopilotTest { get; init; }
    public bool SetupLabels { get; init; }
    public string? AttachmentTestUrl { get; init; }

    /// <summary>The issue <c>--close-test</c> closes, as <c>owner/repo#n</c>.</summary>
    public string? CloseTestIssue { get; init; }

    public string? ConfigPath { get; init; }

    /// <summary>Where <c>--openapi</c> writes the dashboard API document (for the generated UI client).</summary>
    public string? OpenApiPath { get; init; }

    /// <summary>The normal mode: poll every interval until Ctrl+C (with <c>--dry-run</c>: plan only).</summary>
    public bool IsPollingLoop =>
        !RunOnce && !CopilotTest && !SetupLabels && AttachmentTestUrl is null && CloseTestIssue is null &&
        OpenApiPath is null;

    public static RunMode Parse(string[] arguments) => new()
    {
        RunOnce = arguments.Contains("--once"),
        DryRun = arguments.Contains("--dry-run"),
        CopilotTest = arguments.Contains("--copilot-test"),
        SetupLabels = arguments.Contains("--setup-labels"),
        AttachmentTestUrl = ValueOf(arguments, "--attachment-test"),
        CloseTestIssue = ValueOf(arguments, "--close-test"),
        ConfigPath = ValueOf(arguments, "--config"),
        OpenApiPath = ValueOf(arguments, "--openapi"),
    };

    /// <summary>
    /// With no token, only the real loop waits in setup mode (dashboard up, no code-host call, no job); every other
    /// mode needs the token at once and stops with <see cref="NoTokenMessage"/>.
    /// </summary>
    public bool CanStartInSetupMode => IsPollingLoop && !DryRun;

    /// <summary>
    /// Only the real loop serves the dashboard: a one-off mode or a dry run must never open a port. In setup mode it
    /// is served even when disabled, since the Setup page is the way out (still on 127.0.0.1 only).
    /// </summary>
    public bool ShouldStartDashboard(DashboardConfig dashboard, bool isSetupMode = false) =>
        (dashboard.Enabled || isSetupMode) && IsPollingLoop && !DryRun;

    /// <summary>Why a mode other than the loop cannot start without a token, and the two ways to give one.</summary>
    public static string NoTokenMessage(int port) =>
        $"No GitBucket token. Start the runner without flags and open {SetupUrl(port)}, or run: " +
        "dotnet user-secrets set GitBucket:Token <token> --project src/AiRunner";

    public static string SetupUrl(int port) => $"http://localhost:{port}/setup";

    private static string? ValueOf(string[] arguments, string name)
    {
        var argumentIndex = Array.IndexOf(arguments, name);
        var hasValue = argumentIndex >= 0 && argumentIndex + 1 < arguments.Length;

        return hasValue ? arguments[argumentIndex + 1] : null;
    }
}
