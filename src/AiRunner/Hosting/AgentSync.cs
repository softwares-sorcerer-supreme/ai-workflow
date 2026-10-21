using AiRunner.Infrastructure;

namespace AiRunner.Hosting;

/// <summary>
/// Copies the shared agents and skills to the Copilot home between jobs (the **Sync** of CONTEXT.md), so one Copilot
/// run never mixes old and new agents. It runs at once when no job runs, else right after the running job (the worker
/// holds the job slot of <see cref="RunnerCommands"/> for a whole job). Requests that come while one is still waiting
/// share it: the copy reads the files when it runs, so one run covers them all.
/// </summary>
public sealed class AgentSync(
    AgentInstaller installer,
    CopilotHome copilotHome,
    RunnerCommands commands,
    ILogger<AgentSync> logger)
{
    private readonly Lock _lock = new();
    private Task? _waiting;

    /// <summary>Asks for a sync. The task ends when it is done; the dashboard does not wait for it.</summary>
    public Task Request()
    {
        lock (_lock)
        {
            if (_waiting is not null)
            {
                return _waiting;
            }

            _waiting = Task.Run(RunAsync);

            return _waiting;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await commands.RunBetweenJobsAsync(() => installer.Install(copilotHome.Path), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Agent sync to {Target} failed: {Error}", copilotHome.Path, exception.Message);
        }
        finally
        {
            // From here a new request starts a new sync: the files it would copy may be newer than this one's.
            lock (_lock)
            {
                _waiting = null;
            }
        }
    }
}
