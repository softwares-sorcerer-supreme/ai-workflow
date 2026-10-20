using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Infrastructure;

/// <summary>The arguments of "copilot -p": attached files, and the one-time check that the CLI knows the flag.</summary>
public class CopilotRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Every_attached_file_gets_its_own_attachment_flag()
    {
        var processes = new FakeProcesses();
        var runner = new CopilotRunner(new RunnerConfig(), processes, NullLogger<CopilotRunner>.Instance);
        var request = new CopilotRequest("work", "prompt", null, null, Timeout)
        {
            Attachments = ["C:\\work\\1-a.png", "C:\\work\\2-b.pdf"],
        };

        await runner.RunAsync(request, default);

        var command = processes.Commands.Single();
        Assert.Contains("--attachment C:\\work\\1-a.png --attachment C:\\work\\2-b.pdf", command);
    }

    [Fact]
    public async Task No_attachments_add_no_flag()
    {
        var processes = new FakeProcesses();
        var runner = new CopilotRunner(new RunnerConfig(), processes, NullLogger<CopilotRunner>.Instance);
        var request = new CopilotRequest("work", "prompt", null, null, Timeout);

        await runner.RunAsync(request, default);

        Assert.DoesNotContain("--attachment", processes.Commands.Single());
    }

    [Fact]
    public async Task A_usage_file_is_asked_for_when_the_help_lists_the_flag()
    {
        var processes = new FakeProcesses { StdOut = "Options:\n  --usage-output-file <path>" };
        var runner = new CopilotRunner(new RunnerConfig(), processes, NullLogger<CopilotRunner>.Instance);
        var request = new CopilotRequest("work", "prompt", null, null, Timeout)
        {
            UsageFile = "C:\\logs\\copilot-usage.json",
        };

        await runner.RunAsync(request, default);
        await runner.RunAsync(request, default);

        Assert.Equal(["--help"], processes.Commands.Where(command => command == "--help"));
        Assert.Contains("--usage-output-file C:\\logs\\copilot-usage.json", processes.Commands.Last());
    }

    [Fact]
    public async Task An_older_cli_gets_no_usage_flag()
    {
        var processes = new FakeProcesses { StdOut = "Options:\n  --attachment <path>" };
        var runner = new CopilotRunner(new RunnerConfig(), processes, NullLogger<CopilotRunner>.Instance);
        var request = new CopilotRequest("work", "prompt", null, null, Timeout)
        {
            UsageFile = "C:\\logs\\copilot-usage.json",
        };

        await runner.RunAsync(request, default);

        Assert.DoesNotContain("--usage-output-file", processes.Commands.Last());
    }

    [Theory]
    [InlineData("Options:\n  --attachment <path>   Attach a file", true)]
    [InlineData("Options:\n  --add-dir <directory>", false)]
    public async Task The_flag_is_found_in_the_help_text_and_asked_only_once(string help, bool expected)
    {
        var processes = new FakeProcesses { StdOut = help };
        var runner = new CopilotRunner(new RunnerConfig(), processes, NullLogger<CopilotRunner>.Instance);

        var first = await runner.CanAttachFilesAsync("work", default);
        var second = await runner.CanAttachFilesAsync("work", default);

        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.Single(processes.Commands);
    }
}
