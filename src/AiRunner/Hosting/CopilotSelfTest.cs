using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

/// <summary>--copilot-test (Phase 0 gate): start copilot from .NET and capture the output.</summary>
public sealed class CopilotSelfTest(ICopilotRunner copilot, ILogger<CopilotSelfTest> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("ai-runner-test-").FullName;
        log.LogInformation("Work folder: {Dir}", dir);

        // Test 1: simplest call, tokens removed from the environment.
        log.LogInformation("Test 1: inline prompt");
        var r1 = await copilot.RunAsync(new CopilotRequest(dir, "Reply with the single word OK", null, null, TimeSpan.FromMinutes(2)), ct);
        Report("Test 1", r1);

        // Test 2: same shape as the real jobs - the prompt only names a file,
        // the agent may read but not write or run shell, and we extract a marked block.
        await File.WriteAllTextAsync(Path.Combine(dir, "task.md"),
            "Write exactly the three lines below, with nothing before or after them:\n\n" +
            "<!-- TEST_START -->\nOK from task file\n<!-- TEST_END -->\n", ct);

        log.LogInformation("Test 2: prompt names a file, write and shell denied");
        var r2 = await copilot.RunAsync(
            new CopilotRequest(dir, "Read the file task.md in the current folder and do exactly what it says.", null, null,
                TimeSpan.FromMinutes(3)) { DenyTools = ["write", "shell"] }, ct);
        Report("Test 2", r2);
        log.LogInformation("Test 2 extracted block: {Block}", Markers.Extract(r2.StdOut, "TEST") ?? "(markers not found)");
    }

    private void Report(string name, ProcessResult r)
    {
        log.LogInformation("{Name}: ok={Ok} exit={Exit} {Seconds:F1}s", name, r.Ok, r.ExitCode, r.Duration.TotalSeconds);
        log.LogInformation("{Name} stdout: {Out}", name, Text.Short(r.StdOut, 600));
        if (!string.IsNullOrWhiteSpace(r.StdErr))
            log.LogWarning("{Name} stderr: {Err}", name, Text.Short(r.StdErr, 600));
    }
}
