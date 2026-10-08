using System.Diagnostics;
using AiRunner.Infrastructure;

namespace AiRunner.Tests.Infrastructure;

public class ProcessRunnerTests
{
    private static readonly string Dir = Path.GetTempPath();
    private static ProcessSpec Shell(string windows, string unix, TimeSpan timeout) =>
        ProcessSpec.Shell(OperatingSystem.IsWindows() ? windows : unix, Dir, timeout);

    [Fact]
    public async Task Timeout_kills_the_process()
    {
        var sw = Stopwatch.StartNew();
        var r = await new ProcessRunner(TimeSpan.FromSeconds(2))
            .RunAsync(Shell("ping -n 30 127.0.0.1", "sleep 30", TimeSpan.FromSeconds(1)), default);

        Assert.True(r.TimedOut);
        Assert.False(r.Ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Grandchild_holding_the_output_pipe_does_not_hang_the_runner()
    {
        // The shell exits at once, but a background child keeps stdout open for 30 s
        // (like an MSBuild node or a test host).
        var sw = Stopwatch.StartNew();
        var r = await new ProcessRunner(TimeSpan.FromSeconds(2)).RunAsync(
            Shell("start /b ping -n 30 127.0.0.1 >nul & echo parent-done", "sleep 30 & echo parent-done", TimeSpan.FromMinutes(1)), default);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("parent-done", r.StdOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Tokens_are_not_passed_to_child_processes()
    {
        Environment.SetEnvironmentVariable("GITBUCKET_TOKEN", "secret-123");
        try
        {
            var r = await new ProcessRunner().RunAsync(
                Shell("echo [%GITBUCKET_TOKEN%]", "echo \"[${GITBUCKET_TOKEN}]\"", TimeSpan.FromSeconds(30)), default);
            Assert.DoesNotContain("secret-123", r.StdOut);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITBUCKET_TOKEN", null);
        }
    }
}
