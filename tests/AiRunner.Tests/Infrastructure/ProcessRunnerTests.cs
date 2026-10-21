using System.Diagnostics;
using AiRunner.Infrastructure;

namespace AiRunner.Tests.Infrastructure;

public class ProcessRunnerTests
{
    private static readonly string WorkFolder = Path.GetTempPath();

    private static ProcessSpec Shell(string windows, string unix, TimeSpan timeout)
    {
        var command = OperatingSystem.IsWindows() ? windows : unix;

        return ProcessSpec.Shell(command, WorkFolder, timeout);
    }

    [Fact]
    public async Task Timeout_kills_the_process()
    {
        var stopwatch = Stopwatch.StartNew();
        var drainTimeout = TimeSpan.FromSeconds(2);
        var runner = new ProcessRunner(drainTimeout);
        var commandTimeout = TimeSpan.FromSeconds(1);
        var spec = Shell("ping -n 30 127.0.0.1", "sleep 30", commandTimeout);
        var result = await runner.RunAsync(spec, default);

        Assert.True(result.TimedOut);
        Assert.False(result.Ok);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Grandchild_holding_the_output_pipe_does_not_hang_the_runner()
    {
        // The shell exits at once, but a background child keeps stdout open for 30 s
        // (like an MSBuild node or a test host).
        var stopwatch = Stopwatch.StartNew();
        var drainTimeout = TimeSpan.FromSeconds(2);
        var runner = new ProcessRunner(drainTimeout);
        var commandTimeout = TimeSpan.FromMinutes(1);
        var spec = Shell(
            "start /b ping -n 30 127.0.0.1 >nul & echo parent-done",
            "sleep 30 & echo parent-done",
            commandTimeout);
        var result = await runner.RunAsync(spec, default);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("parent-done", result.StdOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Live_file_grows_while_the_process_runs_and_the_result_is_unchanged()
    {
        var liveFile = Path.Combine(Directory.CreateTempSubdirectory("ai-runner-live-").FullName, "live.txt");
        var runner = new ProcessRunner();
        var commandTimeout = TimeSpan.FromSeconds(60);
        var spec = Shell(
            "echo first& ping -n 6 127.0.0.1 >nul& echo second",
            "echo first; sleep 5; echo second",
            commandTimeout) with
        {
            LiveOutputFile = liveFile,
        };

        var running = runner.RunAsync(spec, default);
        var textWhileRunning = "";
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!textWhileRunning.Contains("first") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            textWhileRunning = ReadShared(liveFile);
        }

        Assert.Contains("first", textWhileRunning);
        Assert.DoesNotContain("second", textWhileRunning);
        Assert.False(running.IsCompleted);

        var result = await running;
        var finalText = ReadShared(liveFile);

        Assert.Contains("second", finalText);
        Assert.Equal("first\nsecond\n", finalText.Replace("\r", ""));
        Assert.Equal("first\nsecond\n", result.StdOut.Replace("\r", ""));
        Assert.Equal("", result.StdErr);
    }

    [Fact]
    public async Task Live_file_that_cannot_be_opened_does_not_fail_the_process()
    {
        var missingFolder = Path.Combine(Path.GetTempPath(), "ai-runner-no-such-" + Guid.NewGuid(), "live.txt");
        var runner = new ProcessRunner();
        var commandTimeout = TimeSpan.FromSeconds(30);
        var spec = Shell("echo hello", "echo hello", commandTimeout) with { LiveOutputFile = missingFolder };

        var result = await runner.RunAsync(spec, default);

        Assert.True(result.Ok);
        Assert.Contains("hello", result.StdOut);
    }

    private static string ReadShared(string file)
    {
        if (!File.Exists(file))
        {
            return "";
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Tokens_are_not_passed_to_child_processes()
    {
        Environment.SetEnvironmentVariable("GITBUCKET_TOKEN", "secret-123");

        try
        {
            var runner = new ProcessRunner();
            var commandTimeout = TimeSpan.FromSeconds(30);
            var spec = Shell("echo [%GITBUCKET_TOKEN%]", "echo \"[${GITBUCKET_TOKEN}]\"", commandTimeout);
            var result = await runner.RunAsync(spec, default);

            Assert.DoesNotContain("secret-123", result.StdOut);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITBUCKET_TOKEN", null);
        }
    }
}
