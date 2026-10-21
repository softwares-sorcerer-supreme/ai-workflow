using System.Text.Json;
using AiRunner.Infrastructure;

namespace AiRunner.Tests.Infrastructure;

/// <summary>The user-secrets file the Setup page writes: always a temp file here, never the real one.</summary>
public sealed class UserSecretsStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ai-runner-secrets-").FullName;

    public void Dispose() => FileSystem.DeleteDirectory(_folder);

    private string SecretsFile => Path.Combine(_folder, "secrets.json");

    private UserSecretsStore Store() => new(SecretsFile);

    private Dictionary<string, string> FileContent()
    {
        var text = File.ReadAllText(SecretsFile);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(text)!;
    }

    [Fact]
    public void A_write_keeps_every_other_key()
    {
        File.WriteAllText(SecretsFile, """
            {
              "Other:ApiKey": "keep-me",
              "GitBucket:GitPassword": "git-pass",
              "Nested": { "Child": "nested-value", "List": [ "a", "b" ] }
            }
            """);

        Store().Set(UserSecretsStore.TokenKey, "new-token");

        var content = FileContent();
        Assert.Equal("new-token", content["GitBucket:Token"]);
        Assert.Equal("keep-me", content["Other:ApiKey"]);
        Assert.Equal("git-pass", content["GitBucket:GitPassword"]);
        Assert.Equal("nested-value", content["Nested:Child"]);
        Assert.Equal("b", content["Nested:List:1"]);
    }

    [Fact]
    public void A_write_replaces_the_value_and_leaves_no_temp_file()
    {
        File.WriteAllText(SecretsFile, """{ "GitBucket:Token": "old-token" }""");

        Store().Set(UserSecretsStore.TokenKey, "new-token");

        Assert.Equal("new-token", Store().Get(UserSecretsStore.TokenKey));
        Assert.Equal([SecretsFile], Directory.GetFiles(_folder));
    }

    [Fact]
    public void A_nested_key_is_replaced_by_the_flat_one()
    {
        File.WriteAllText(SecretsFile, """{ "GitBucket": { "Token": "old-token", "WebPassword": "web" } }""");

        Store().Set(UserSecretsStore.TokenKey, "new-token");

        var content = FileContent();
        Assert.Equal("new-token", content["GitBucket:Token"]);
        Assert.Equal("web", content["GitBucket:WebPassword"]);
        Assert.Equal(2, content.Count);
    }

    [Fact]
    public void A_missing_file_and_folder_are_created()
    {
        var deepFile = Path.Combine(_folder, "UserSecrets", "id-1", "secrets.json");
        var store = new UserSecretsStore(deepFile);

        Assert.Empty(store.Read());

        store.Set(UserSecretsStore.WebPasswordKey, "web");

        Assert.Equal("web", new UserSecretsStore(deepFile).Get(UserSecretsStore.WebPasswordKey));
    }

    [Fact]
    public void Remove_takes_out_one_key_only()
    {
        File.WriteAllText(SecretsFile, """{ "GitBucket:Token": "t", "Other": "keep" }""");

        Store().Remove(UserSecretsStore.TokenKey);

        var content = FileContent();
        Assert.False(content.ContainsKey("GitBucket:Token"));
        Assert.Equal("keep", content["Other"]);
    }

    [Fact]
    public void Keys_are_read_without_regard_to_case_like_the_configuration_reader()
    {
        File.WriteAllText(SecretsFile, """{ "gitbucket:token": "t" }""");

        Assert.Equal("t", Store().Get(UserSecretsStore.TokenKey));
    }

    [Fact]
    public void A_broken_file_is_refused_without_quoting_it_and_never_overwritten()
    {
        const string Broken = """{ "GitBucket:Token": "s3cret-in-a-broken-file" """;
        File.WriteAllText(SecretsFile, Broken);

        var readError = Assert.Throws<InvalidDataException>(() => Store().Read());
        var writeError = Assert.Throws<InvalidDataException>(() => Store().Set(UserSecretsStore.TokenKey, "new"));

        Assert.DoesNotContain("s3cret", readError.Message);
        Assert.DoesNotContain("s3cret", writeError.Message);
        Assert.Contains(SecretsFile, readError.Message);
        Assert.Equal(Broken, File.ReadAllText(SecretsFile));
    }

    [Fact]
    public void The_runner_uses_the_secrets_file_of_its_user_secrets_id()
    {
        var store = UserSecretsStore.ForAssembly(typeof(UserSecretsStore).Assembly);

        Assert.Contains("ai-runner-3f6b2c1e", store.FilePath);
        Assert.EndsWith("secrets.json", store.FilePath);
    }
}
