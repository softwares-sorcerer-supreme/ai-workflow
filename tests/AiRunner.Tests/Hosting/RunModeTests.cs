using AiRunner.Hosting;

namespace AiRunner.Tests.Hosting;

/// <summary>Which command-line mode serves the dashboard: only the real polling loop, and only when enabled.</summary>
public class RunModeTests
{
    private static readonly DashboardConfig Enabled = new() { Enabled = true };

    [Fact]
    public void The_polling_loop_starts_the_dashboard_when_it_is_enabled()
    {
        var mode = RunMode.Parse([]);

        Assert.True(mode.ShouldStartDashboard(Enabled));
    }

    [Fact]
    public void The_polling_loop_does_not_start_a_disabled_dashboard()
    {
        var mode = RunMode.Parse(["--config", "repos.yml"]);

        Assert.False(mode.ShouldStartDashboard(new DashboardConfig()));
    }

    [Theory]
    [InlineData("--once")]
    [InlineData("--dry-run")]
    [InlineData("--once", "--dry-run")]
    [InlineData("--setup-labels")]
    [InlineData("--copilot-test")]
    [InlineData("--attachment-test", "http://gb.test/_attached/1")]
    [InlineData("--close-test", "o/r#7")]
    [InlineData("--openapi", "openapi.json")]
    public void Other_modes_never_start_the_dashboard(params string[] arguments)
    {
        var mode = RunMode.Parse(arguments);

        Assert.False(mode.ShouldStartDashboard(Enabled));
    }

    [Fact]
    public void Flags_with_a_value_are_read()
    {
        var mode = RunMode.Parse(["--config", "other.yml", "--openapi", "out/openapi.json"]);

        Assert.Equal("other.yml", mode.ConfigPath);
        Assert.Equal("out/openapi.json", mode.OpenApiPath);
        Assert.False(mode.IsPollingLoop);
    }
}
