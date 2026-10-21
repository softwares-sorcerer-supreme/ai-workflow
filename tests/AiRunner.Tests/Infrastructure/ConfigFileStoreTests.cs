using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Infrastructure;

/// <summary>Writing repos.yml: the old file is kept as a backup (the last 10), the new one replaces it whole.</summary>
public sealed class ConfigFileStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("config-store-").FullName;
    private readonly FakeTime _time = new();

    private string ConfigPath => Path.Combine(_folder, "repos.yml");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string[] Backups() =>
        Directory.GetFiles(_folder, "repos.yml.bak-*")
            .Select(Path.GetFileName)
            .Order()
            .ToArray()!;

    [Fact]
    public void A_write_replaces_the_file_and_keeps_the_old_one_as_a_backup()
    {
        File.WriteAllText(ConfigPath, "old: 1\n");
        var store = new ConfigFileStore(_time);

        store.Write(ConfigPath, "new: 2\n");

        Assert.Equal("new: 2\n", File.ReadAllText(ConfigPath));
        var backup = Assert.Single(Backups());
        Assert.Equal("repos.yml.bak-20260101T120000000Z", backup);
        Assert.Equal("old: 1\n", File.ReadAllText(Path.Combine(_folder, backup)));
        Assert.Equal(["repos.yml", backup], Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Only_the_last_ten_backups_are_kept()
    {
        File.WriteAllText(ConfigPath, "version: 0\n");
        var store = new ConfigFileStore(_time);

        for (var version = 1; version <= 12; version++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            store.Write(ConfigPath, $"version: {version}\n");
        }

        var backups = Backups();
        Assert.Equal(10, backups.Length);
        Assert.Equal("version: 2\n", File.ReadAllText(Path.Combine(_folder, backups[0])));
        Assert.Equal("version: 11\n", File.ReadAllText(Path.Combine(_folder, backups[^1])));
        Assert.Equal("version: 12\n", File.ReadAllText(ConfigPath));
    }
}
