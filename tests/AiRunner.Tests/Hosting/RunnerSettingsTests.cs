using System.Text.Json.Nodes;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Hosting;

/// <summary>
/// A save from the Settings screen: checked like at startup, written with a backup, applied at once. A stale or an
/// invalid save changes nothing.
/// </summary>
public sealed class RunnerSettingsTests : IDisposable
{
    private const string Start = """
        # a comment
        code_host: { kind: gitbucket, base_url: http://host, web_password: hunter2 }
        poll_interval_seconds: 60
        defaults: { limits: { max_diff_lines: 900 } }
        projects:
          p:
            components: { c: { repo: o/r } }
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("runner-settings-").FullName;
    private readonly RunnerCommands _commands = new();
    private readonly RunnerConfigSource _source;
    private readonly RunnerSettings _settings;

    public RunnerSettingsTests()
    {
        File.WriteAllText(ConfigPath, Start);
        _source = RunnerConfigSource.Load(ConfigPath);
        var store = new ConfigFileStore(new FakeTime());
        _settings = new RunnerSettings(_source, store, _commands, NullLogger<RunnerSettings>.Instance);
    }

    private string ConfigPath => Path.Combine(_folder, "repos.yml");

    public void Dispose() => FileSystem.DeleteDirectory(_folder);

    private string[] FolderFiles() => Directory.GetFiles(_folder).Select(Path.GetFileName).Order().ToArray()!;

    private bool IsWakePending() => _commands.WaitForWakeAsync(default).IsCompleted;

    [Fact]
    public void Read_shows_the_file_without_its_secret_and_with_its_hash()
    {
        var view = _settings.Read();

        Assert.Equal(RunnerConfigSource.HashOf(Start), view.Hash);
        Assert.Equal(1, view.Version);
        Assert.True(view.HasComments);
        Assert.Null(view.Error);
        Assert.Equal(ConfigPath, view.Path);
        Assert.DoesNotContain("hunter2", view.File!.ToJsonString());
        Assert.Equal("p", view.Effective.Single().Project);
    }

    [Fact]
    public async Task A_save_writes_a_backup_applies_the_config_and_wakes_the_poller()
    {
        var view = _settings.Read();
        view.File!["poll_interval_seconds"] = 15;

        var result = _settings.Save(new SettingsSave(view.File, view.Hash));

        Assert.Equal(SettingsSaveOutcome.Saved, result.Outcome);
        Assert.Equal(15, _source.Current.PollIntervalSeconds);
        Assert.Equal(2, _source.State.Version);
        Assert.Equal(15, RunnerConfig.Load(ConfigPath).PollIntervalSeconds);
        Assert.Contains("hunter2", File.ReadAllText(ConfigPath));
        Assert.Equal(2, FolderFiles().Length);
        Assert.True(IsWakePending());
        Assert.False(_settings.Read().HasComments);
    }

    [Fact]
    public async Task A_save_from_an_old_read_is_refused_as_stale()
    {
        var view = _settings.Read();
        var handEdit = Start.Replace("poll_interval_seconds: 60", "poll_interval_seconds: 61");
        File.WriteAllText(ConfigPath, handEdit);
        view.File!["poll_interval_seconds"] = 15;

        var result = _settings.Save(new SettingsSave(view.File, view.Hash));

        Assert.Equal(SettingsSaveOutcome.Stale, result.Outcome);
        Assert.Equal(handEdit, File.ReadAllText(ConfigPath));
        Assert.Single(FolderFiles());
    }

    [Fact]
    public async Task An_invalid_save_changes_nothing()
    {
        var view = _settings.Read();
        view.File!["defaults"]!["limits"]!["max_reviews"] = 0;

        var result = _settings.Save(new SettingsSave(view.File, view.Hash));

        Assert.Equal(SettingsSaveOutcome.Invalid, result.Outcome);
        Assert.Contains("max_reviews must be 1 or more", result.Message);
        Assert.Equal(Start, File.ReadAllText(ConfigPath));
        Assert.Single(FolderFiles());
        Assert.Equal(1, _source.State.Version);
        Assert.False(IsWakePending());
    }

    [Fact]
    public async Task A_token_key_is_refused_and_never_written()
    {
        var view = _settings.Read();
        view.File!["code_host"]!["token"] = "secret-token";

        var result = _settings.Save(new SettingsSave(view.File, view.Hash));

        Assert.Equal(SettingsSaveOutcome.Invalid, result.Outcome);
        Assert.DoesNotContain("secret-token", result.Message);
        Assert.DoesNotContain("secret-token", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task A_restart_needed_change_is_saved_not_applied_and_named()
    {
        var view = _settings.Read();
        view.File!["code_host"]!["base_url"] = "http://other";

        var result = _settings.Save(new SettingsSave(view.File, view.Hash));

        Assert.Equal(SettingsSaveOutcome.Saved, result.Outcome);
        Assert.Contains("code_host.base_url", result.Message);
        Assert.Equal("http://host", _source.Current.CodeHost.BaseUrl);
        Assert.Equal(["code_host.base_url"], _settings.Read().RestartNeeded);
        Assert.Contains("http://other", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void A_broken_hand_edit_is_reported_and_the_last_good_file_is_shown()
    {
        File.WriteAllText(ConfigPath, "poll_interval_seconds: [\n");

        var view = _settings.Read();

        Assert.NotNull(view.Error);
        Assert.Equal(RunnerConfigSource.HashOf("poll_interval_seconds: [\n"), view.Hash);
        Assert.Equal(60, view.File!["poll_interval_seconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_save_over_a_broken_file_keeps_the_secret_of_the_last_good_one()
    {
        File.WriteAllText(ConfigPath, "poll_interval_seconds: [\n");
        var view = _settings.Read();

        var result = _settings.Save(new SettingsSave(view.File!, view.Hash));

        Assert.Equal(SettingsSaveOutcome.Saved, result.Outcome);
        Assert.Contains("hunter2", File.ReadAllText(ConfigPath));
        Assert.Null(_settings.Read().Error);
    }

    [Fact]
    public async Task A_secret_sent_as_null_keeps_its_value()
    {
        var view = _settings.Read();
        view.File!["code_host"]!["web_password"] = null;

        _settings.Save(new SettingsSave(view.File, view.Hash));

        Assert.Contains("hunter2", File.ReadAllText(ConfigPath));
        Assert.IsType<JsonObject>(_settings.Read().File!["code_host"]!["web_password"]);
    }
}
