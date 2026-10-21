namespace AiRunner.Tests.Configuration;

/// <summary>
/// The live config: a hand edit of repos.yml is applied, a broken one never replaces the working config, and the
/// keys that need a restart keep their startup values.
/// </summary>
public sealed class RunnerConfigSourceTests : IDisposable
{
    private const string Start = """
        code_host: { kind: gitbucket, base_url: http://host }
        poll_interval_seconds: 60
        paths: { work: work }
        dashboard: { enabled: true, port: 5180 }
        projects:
          p:
            components: { c: { repo: o/r } }
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("config-source-").FullName;

    private string ConfigPath => Path.Combine(_folder, "repos.yml");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private RunnerConfigSource LoadStart()
    {
        File.WriteAllText(ConfigPath, Start);

        return RunnerConfigSource.Load(ConfigPath);
    }

    [Fact]
    public void A_hand_edit_is_applied_and_counted()
    {
        var source = LoadStart();
        var before = source.Current;
        File.WriteAllText(ConfigPath, Start.Replace("poll_interval_seconds: 60", "poll_interval_seconds: 30"));

        var reload = source.ReloadIfChanged();

        Assert.True(reload.IsChanged);
        Assert.Null(reload.Error);
        Assert.Equal(30, source.Current.PollIntervalSeconds);
        Assert.Equal(60, before.PollIntervalSeconds);
        Assert.Equal(2, source.Version);
        Assert.Equal(_folder, source.Current.RootDir);
    }

    [Fact]
    public void An_unchanged_file_is_not_read_again()
    {
        var source = LoadStart();
        var before = source.Current;

        var reload = source.ReloadIfChanged();

        Assert.False(reload.IsChanged);
        Assert.Same(before, source.Current);
        Assert.Equal(1, source.Version);
    }

    [Fact]
    public void A_broken_hand_edit_keeps_the_old_config_and_reports_the_error_until_fixed()
    {
        var source = LoadStart();
        var before = source.Current;
        File.WriteAllText(ConfigPath, Start + "\nmystery_key: 1\n");

        var broken = source.ReloadIfChanged();

        Assert.True(broken.IsChanged);
        Assert.Contains("unknown key 'mystery_key'", broken.Error);
        Assert.Same(before, source.Current);
        Assert.Contains("unknown key 'mystery_key'", source.Error);
        Assert.Equal(1, source.Version);

        Assert.False(source.ReloadIfChanged().IsChanged);
        Assert.NotNull(source.Error);

        File.WriteAllText(ConfigPath, Start.Replace("60", "45"));
        source.ReloadIfChanged();

        Assert.Null(source.Error);
        Assert.Equal(45, source.Current.PollIntervalSeconds);
    }

    [Fact]
    public void A_missing_base_url_is_refused_like_at_startup()
    {
        var source = LoadStart();
        File.WriteAllText(ConfigPath, Start.Replace("base_url: http://host", "base_url: ''"));

        var reload = source.ReloadIfChanged();

        Assert.Contains("base_url", reload.Error);
        Assert.Equal("http://host", source.Current.CodeHost.BaseUrl);
    }

    [Fact]
    public void Restart_needed_keys_keep_their_startup_values_and_are_listed()
    {
        var source = LoadStart();
        var edited = Start
            .Replace("http://host", "http://other")
            .Replace("work: work", "work: elsewhere")
            .Replace("port: 5180", "port: 6000")
            .Replace("poll_interval_seconds: 60", "poll_interval_seconds: 60\nrunner_name: renamed");
        File.WriteAllText(ConfigPath, edited);

        source.ReloadIfChanged();

        var config = source.Current;
        Assert.Equal("http://host", config.CodeHost.BaseUrl);
        Assert.Equal("work", config.Paths.Work);
        Assert.Equal(5180, config.Dashboard.Port);
        Assert.Equal("", config.RunnerName);
        Assert.Equal(
            ["code_host.base_url", "dashboard.port", "paths.work", "runner_name"],
            source.RestartNeeded.Order());
    }

    [Fact]
    public void Going_back_to_the_startup_values_clears_the_restart_list()
    {
        var source = LoadStart();
        File.WriteAllText(ConfigPath, Start.Replace("port: 5180", "port: 6000"));
        source.ReloadIfChanged();

        File.WriteAllText(ConfigPath, Start);
        source.ReloadIfChanged();

        Assert.Empty(source.RestartNeeded);
    }

    [Fact]
    public void Applying_saved_text_publishes_it_without_reading_the_file()
    {
        var source = LoadStart();
        var saved = Start.Replace("poll_interval_seconds: 60", "poll_interval_seconds: 20");

        source.Apply(saved);

        Assert.Equal(20, source.Current.PollIntervalSeconds);
        Assert.Equal(saved, source.Text);
        Assert.Equal(RunnerConfigSource.HashOf(saved), source.FileHash);
    }

    [Fact]
    public void A_fixed_source_never_changes()
    {
        var config = new RunnerConfig { PollIntervalSeconds = 7 };

        var source = RunnerConfigSource.Fixed(config);

        Assert.Same(config, source.Current);
        Assert.False(source.ReloadIfChanged().IsChanged);
    }
}
