using System.Diagnostics;
using System.Text;

namespace AiRunner.Infrastructure;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args, string WorkDir, TimeSpan Timeout)
{
    public IReadOnlyDictionary<string, string> SetEnv { get; init; } = new Dictionary<string, string>();
    /// <summary>Raw argument string, used instead of Args (cmd.exe needs its own quoting).</summary>
    public string? RawArguments { get; init; }

    /// <summary>A command line from repos.yml ("npm test"), run by the OS shell.</summary>
    public static ProcessSpec Shell(string command, string workDir, TimeSpan timeout) =>
        OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", [], workDir, timeout) { RawArguments = $"/d /s /c \"{command}\"" }
            : new ProcessSpec("/bin/sh", ["-c", command], workDir, timeout);
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration, bool TimedOut)
{
    public bool Ok => !TimedOut && ExitCode == 0;

    /// <summary>Last part of the output, for a log line or a comment.</summary>
    public string Tail(int chars = 1500)
    {
        var output = (StdOut + "\n" + StdErr).Trim();
        return output.Length <= chars ? output : "..." + output[^chars..];
    }

    /// <summary>The command, its exit code and its full output, for the job's log folder.</summary>
    public string Report(string command) => $"$ {command}\nexit {ExitCode}\n\n{StdOut}\n{StdErr}";
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
}

/// <summary>
/// Starts a child process with no keyboard input, captures its output and kills the whole
/// process tree on timeout. Used for copilot, git and the repo's build/test commands.
/// Every child runs without the runner's tokens in its environment: build and test commands
/// execute code the agent wrote.
/// </summary>
public sealed class ProcessRunner(TimeSpan? drainTimeout = null) : IProcessRunner
{
    /// <summary>
    /// Removed from every child. COPILOT_GITHUB_TOKEN is kept on purpose: it is the explicit
    /// way to give Copilot a seat token. "__" is how .NET maps config keys to environment variables.
    /// </summary>
    private static readonly string[] SecretVars =
    [
        "GITBUCKET_TOKEN", "GitBucket__Token", "GitBucket__GitPassword", "GitBucket__WebPassword",
        "GH_TOKEN", "GITHUB_TOKEN", "AIRUNNER_GITHUB_TOKEN",
    ];

    /// <summary>
    /// After the process exits, a grandchild (build server, test host, background shell) may still
    /// hold the output pipes open. Wait this long for the rest of the output, then go on without it.
    /// </summary>
    private readonly TimeSpan _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(15);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (spec.RawArguments is not null)
        {
            startInfo.Arguments = spec.RawArguments;
        }
        else
        {
            foreach (var argument in spec.Args)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        var secretNames = startInfo.Environment.Keys
            .Where(environmentKey => SecretVars.Contains(environmentKey, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var name in secretNames)
        {
            startInfo.Environment.Remove(name);
        }

        foreach (var (variableName, variableValue) in spec.SetEnv)
        {
            startInfo.Environment[variableName] = variableValue;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The Exited event, not WaitForExitAsync: with async output reading, WaitForExitAsync also
        // waits for the end of the output, which a grandchild holding the pipe delays forever.
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => exited.TrySetResult();
        process.OutputDataReceived += (_, eventArgs) => Collect(stdout, stdoutDone, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => Collect(stderr, stderrDone, eventArgs.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{spec.FileName}'");
        }

        process.StandardInput.Close(); // never wait for keyboard input
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (process.HasExited)
        {
            exited.TrySetResult();
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(spec.Timeout);
        var timedOut = false;

        try
        {
            await exited.Task.WaitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process had already exited.
            }

            var exitGracePeriod = TimeSpan.FromSeconds(30);
            await exited.Task.WaitAsync(exitGracePeriod, CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(_drainTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // Keep what was read so far.
        }

        string Text(StringBuilder buffer)
        {
            lock (buffer)
            {
                return buffer.ToString();
            }
        }

        var stdoutText = Text(stdout);
        var stderrText = Text(stderr);
        var result = new ProcessResult(process.ExitCode, stdoutText, stderrText, stopwatch.Elapsed, timedOut);
        cancellationToken.ThrowIfCancellationRequested(); // runner is stopping: do not treat as a job result
        return result;
    }

    private static void Collect(StringBuilder buffer, TaskCompletionSource done, string? line)
    {
        if (line is null)
        {
            done.TrySetResult();
            return;
        }

        lock (buffer)
        {
            buffer.Append(line).Append('\n');
        }
    }
}
