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
    public string? ConfigPath { get; init; }

    /// <summary>Where <c>--openapi</c> writes the dashboard API document (for the generated UI client).</summary>
    public string? OpenApiPath { get; init; }

    /// <summary>The normal mode: poll every interval until Ctrl+C (with <c>--dry-run</c>: plan only).</summary>
    public bool IsPollingLoop =>
        !RunOnce && !CopilotTest && !SetupLabels && AttachmentTestUrl is null && OpenApiPath is null;

    public static RunMode Parse(string[] arguments) => new()
    {
        RunOnce = arguments.Contains("--once"),
        DryRun = arguments.Contains("--dry-run"),
        CopilotTest = arguments.Contains("--copilot-test"),
        SetupLabels = arguments.Contains("--setup-labels"),
        AttachmentTestUrl = ValueOf(arguments, "--attachment-test"),
        ConfigPath = ValueOf(arguments, "--config"),
        OpenApiPath = ValueOf(arguments, "--openapi"),
    };

    /// <summary>Only the real loop serves the dashboard: a one-off mode or a dry run must never open a port.</summary>
    public bool ShouldStartDashboard(DashboardConfig dashboard) => dashboard.Enabled && IsPollingLoop && !DryRun;

    private static string? ValueOf(string[] arguments, string name)
    {
        var argumentIndex = Array.IndexOf(arguments, name);
        var hasValue = argumentIndex >= 0 && argumentIndex + 1 < arguments.Length;

        return hasValue ? arguments[argumentIndex + 1] : null;
    }
}
