using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Hosting;

/// <summary>
/// Setup mode: the loop with no token makes no code-host call and runs no job, and a token saved on the Setup page
/// starts the polling without a restart.
/// </summary>
public sealed class SetupModeTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly SetupFixture _setup = new();
    private readonly JobTestKit _kit;

    public SetupModeTests()
    {
        _kit = new JobTestKit { Credentials = _setup.Credentials };
        _kit.CodeHost.AddIssue(7, "Export", null, "ai");
    }

    public void Dispose()
    {
        _kit.Dispose();
        _setup.Dispose();
    }

    [Fact]
    public async Task Without_a_token_nothing_reaches_the_code_host_until_one_is_saved()
    {
        var job = new StubJob(() => Task.FromResult(JobOutcome.Ok()));
        var (worker, queue, _) = _kit.CreateWorker(job);
        var request = _kit.Request(JobType.AnalyzeIssue, 7);
        queue.Enqueue(request);
        var poller = _kit.NewPoller(queue);
        using var stop = new CancellationTokenSource();

        await poller.StartAsync(stop.Token);
        await worker.StartAsync(stop.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var callsInSetupMode = _kit.CodeHost.CallCount;
        var hasRunInSetupMode = job.HasRun;
        var pollInSetupMode = _kit.Status.Snapshot().LastPollStartedAt;

        var setup = _setup.CreateSetup(_kit.Identity, _kit.Commands);
        var result = await setup.SaveTokenAsync("new-token", default);
        await WaitUntilAsync(() => _kit.CodeHost.ReposOf("issues").Any());

        await stop.CancelAsync();
        await poller.StopAsync(default);
        await worker.StopAsync(default);

        Assert.Equal(0, callsInSetupMode);
        Assert.False(hasRunInSetupMode);
        Assert.Null(pollInSetupMode);
        Assert.Equal(SetupOutcome.Saved, result.Outcome);
        Assert.Equal("alice", _kit.Identity.Login);
        Assert.Equal(1, _setup.CheckHost.CurrentUserReads);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not hold in time.");
            }

            await Task.Delay(20);
        }
    }
}
