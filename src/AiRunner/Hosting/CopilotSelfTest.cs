using AiRunner.Core;
using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

/// <summary>--copilot-test (Phase 0 gate): start copilot from .NET and capture the output.</summary>
public sealed class CopilotSelfTest(ICopilotRunner copilotRunner, ILogger<CopilotSelfTest> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("ai-runner-test-").FullName;
        logger.LogInformation("Work folder: {Dir}", directory);

        // Test 1: simplest call, tokens removed from the environment.
        logger.LogInformation("Test 1: inline prompt");
        var inlinePromptTimeout = TimeSpan.FromMinutes(2);
        var inlinePromptRequest = new CopilotRequest(
            directory,
            "Reply with the single word OK",
            null,
            null,
            inlinePromptTimeout);
        var inlinePromptResult = await copilotRunner.RunAsync(inlinePromptRequest, cancellationToken);
        Report("Test 1", inlinePromptResult);

        // Test 2: same shape as the real jobs - the prompt only names a file,
        // the agent may read but not write or run shell, and we extract a marked block.
        var taskFile = Path.Combine(directory, "task.md");
        await File.WriteAllTextAsync(
            taskFile,
            "Write exactly the three lines below, with nothing before or after them:\n\n" +
            "<!-- TEST_START -->\nOK from task file\n<!-- TEST_END -->\n",
            cancellationToken);

        logger.LogInformation("Test 2: prompt names a file, write and shell denied");
        var taskFileTimeout = TimeSpan.FromMinutes(3);
        var taskFileResult = await copilotRunner.RunAsync(
            new CopilotRequest(
                directory,
                "Read the file task.md in the current folder and do exactly what it says.",
                null,
                null,
                taskFileTimeout)
            {
                DenyTools = ["write", "shell"]
            },
            cancellationToken);
        Report("Test 2", taskFileResult);

        var extractedBlock = Markers.Extract(taskFileResult.StdOut, "TEST") ?? "(markers not found)";
        logger.LogInformation("Test 2 extracted block: {Block}", extractedBlock);
    }

    private void Report(string name, ProcessResult result)
    {
        logger.LogInformation("{Name}: ok={Ok} exit={Exit} {Seconds:F1}s", name, result.Ok, result.ExitCode, result.Duration.TotalSeconds);
        var shortOutput = Text.Short(result.StdOut, 600);
        logger.LogInformation("{Name} stdout: {Out}", name, shortOutput);

        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            var shortError = Text.Short(result.StdErr, 600);
            logger.LogWarning("{Name} stderr: {Err}", name, shortError);
        }
    }
}
