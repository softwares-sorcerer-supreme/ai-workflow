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
        await Wait.UntilAsync(() => _kit.CodeHost.ReposOf("issues").Any());

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

    [Fact]
    public async Task A_job_a_crash_left_running_is_reported_only_once_there_is_a_token()
    {
        var job = new StubJob(() => Task.FromResult(JobOutcome.Ok()));
        var (worker, queue, _) = _kit.CreateWorker(job);
        var request = _kit.Request(JobType.AnalyzeIssue, 7);
        queue.Enqueue(request);
        queue.TryDequeue(out var first);
        _kit.State.SetJobStatus(first!.Id, JobStatus.Running);
        var (_, restarted, _) = _kit.CreateWorker(job);
        restarted.Recover();
        restarted.TryDequeue(out var second);
        _kit.State.SetJobStatus(second!.Id, JobStatus.Running);
        using var stop = new CancellationTokenSource();

        await worker.StartAsync(stop.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var callsInSetupMode = _kit.CodeHost.CallCount;

        _setup.Credentials.Apply(RunnerCredentials.Of(token: "new-token").Current);
        await Wait.UntilAsync(() => _kit.CodeHost.CommentsOf(7).Any());

        await stop.CancelAsync();
        await worker.StopAsync(default);

        Assert.Equal(0, callsInSetupMode);
        Assert.Contains("stopped twice", _kit.CodeHost.CommentsOf(7).Single().Body);
    }
}
