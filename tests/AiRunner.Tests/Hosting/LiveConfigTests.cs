using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace AiRunner.Tests.Hosting;

/// <summary>
/// A new config while the runner runs: a running job keeps the config it started with, the next job and the next
/// poll use the new one, and a broken hand edit keeps the runner on the working config.
/// </summary>
public sealed class LiveConfigTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly JobTestKit _kit = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("live-config-").FullName;

    private string ConfigPath => Path.Combine(_folder, "repos.yml");

    public void Dispose()
    {
        _kit.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static string FileWith(int pollSeconds, string extraProject = "") => $$"""
        code_host: { kind: gitbucket, base_url: {{JobTestKit.BaseUrl}} }
        poll_interval_seconds: {{pollSeconds}}
        projects:
          demo:
            components: { demo: { repo: {{JobTestKit.Repo}} } }
        {{extraProject}}
        """;

    private RunnerConfigSource LoadSource(string text)
    {
        File.WriteAllText(ConfigPath, text);

        return RunnerConfigSource.Load(ConfigPath);
    }

    private void HandEdit(RunnerConfigSource source, string text)
    {
        File.WriteAllText(ConfigPath, text);
        source.ReloadIfChanged();
    }

    /// <summary>What the probe jobs saw, and the gate that holds the first one mid-run.</summary>
    private sealed class Probe
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> SeenPollSeconds { get; } = [];
    }

    /// <summary>A job that reads its config at the end of its run, after the first one was held while a new config came.</summary>
    private sealed class ProbeJob(RunnerConfig config, Probe probe) : IJob
    {
        public JobType Type => JobType.AnalyzeIssue;

        public JobInfo Info => new(Workflows.Analyze, "Probe", TimeSpan.FromMinutes(1));

        public async Task<JobOutcome> RunAsync(JobRequest request, JobRun jobRun, CancellationToken cancellationToken)
        {
            if (probe.SeenPollSeconds.Count == 0 && !probe.FirstStarted.Task.IsCompleted)
            {
                probe.FirstStarted.SetResult();
                await probe.ReleaseFirst.Task;
            }

            probe.SeenPollSeconds.Add(config.PollIntervalSeconds);

            return JobOutcome.Ok();
        }
    }

    [Fact]
    public async Task A_running_job_keeps_its_config_while_a_new_one_is_applied()
    {
        var source = LoadSource(FileWith(pollSeconds: 60));
        var probe = new Probe();
        var scopes = _kit.JobScopes(source, services =>
        {
            services.AddSingleton(probe);
            services.AddScoped<IJob, ProbeJob>();
        });
        var (worker, queue) = _kit.NewWorker(source, scopes, _kit.State);
        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 7));
        queue.Enqueue(_kit.Request(JobType.AnalyzeIssue, 8));

        var drain = worker.DrainAsync(default);
        await probe.FirstStarted.Task.WaitAsync(TestTimeout);
        HandEdit(source, FileWith(pollSeconds: 30));
        probe.ReleaseFirst.SetResult();
        await drain.WaitAsync(TestTimeout);

        Assert.Equal(30, source.Current.PollIntervalSeconds);
        Assert.Equal([60, 30], probe.SeenPollSeconds);
    }

    [Fact]
    public async Task A_hand_edit_is_used_by_the_next_poll()
    {
        var source = LoadSource(FileWith(pollSeconds: 60));
        var queue = new JobQueue(_kit.State, Microsoft.Extensions.Logging.Abstractions.NullLogger<JobQueue>.Instance);
        var poller = _kit.NewPoller(queue, configSource: source);
        await poller.PollOnceAsync(default);
        Assert.Equal([JobTestKit.Repo], _kit.CodeHost.ReposOf("issues"));

        File.WriteAllText(ConfigPath, FileWith(60, """
              other:
                components: { other: { repo: o/other } }
            """));
        await poller.PollOnceAsync(default);

        Assert.Equal([JobTestKit.Repo, "o/other"], _kit.CodeHost.ReposOf("issues"));
        Assert.Null(source.Error);
    }

    [Fact]
    public async Task A_broken_hand_edit_keeps_polling_with_the_working_config()
    {
        var source = LoadSource(FileWith(pollSeconds: 60));
        var queue = new JobQueue(_kit.State, Microsoft.Extensions.Logging.Abstractions.NullLogger<JobQueue>.Instance);
        var poller = _kit.NewPoller(queue, configSource: source);
        File.WriteAllText(ConfigPath, FileWith(60) + "\nmystery_key: 1\n");

        await poller.PollOnceAsync(default);

        Assert.Contains("mystery_key", source.Error);
        Assert.Equal([JobTestKit.Repo], _kit.CodeHost.ReposOf("issues"));
        Assert.Equal(1, source.Version);
    }
}
