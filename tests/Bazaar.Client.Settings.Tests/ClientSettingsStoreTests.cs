namespace Bazaar.Client.Settings.Tests;

public sealed class ClientSettingsStoreTests : IDisposable
{
    private static readonly ClientSettingDefinition[] _definitions =
    [
        new("client.theme", "void", ["void", "graphite", "paper"]),
        new("client.motion_level", null, ["liquid", "subtle", "reduced"]),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string FilePath => Path.Combine(_dir, "sub", "client-settings.json");

    [Fact]
    public void Unset_keys_give_their_registered_default()
    {
        var store = new ClientSettingsStore(_definitions);

        Assert.Equal("void", store.GetValue("client.theme"));
        Assert.Null(store.GetValue("client.motion_level"));
    }

    [Fact]
    public void Unregistered_keys_throw()
    {
        var store = new ClientSettingsStore(_definitions);

        Assert.Throws<ArgumentException>(() => store.GetValue("client.nope"));
        Assert.Throws<ArgumentException>(() => store.TrySetValue("client.nope", "x"));
    }

    [Fact]
    public void A_value_the_definition_rejects_changes_nothing()
    {
        var store = new ClientSettingsStore(_definitions);
        bool raised = false;
        store.Changed += (_, _) => raised = true;

        Assert.False(store.TrySetValue("client.theme", "neon"));
        Assert.False(store.TrySetValue("client.theme", "  "));
        Assert.Equal("void", store.GetValue("client.theme"));
        Assert.False(raised);
    }

    [Fact]
    public void Set_normalises_case_insensitive_matches_and_raises_changed_once()
    {
        var store = new ClientSettingsStore(_definitions);
        var seen = new List<(string Key, string? Value)>();
        store.Changed += (_, e) => seen.Add((e.Key, e.Value));

        Assert.True(store.TrySetValue("client.theme", " Graphite "));
        Assert.True(store.TrySetValue("client.theme", "graphite"));

        Assert.Equal("graphite", store.GetValue("client.theme"));
        Assert.Single(seen);
        Assert.Equal(("client.theme", "graphite"), seen[0]);
    }

    [Fact]
    public void Clearing_a_key_returns_to_the_default_and_notifies()
    {
        var store = new ClientSettingsStore(_definitions);
        store.TrySetValue("client.theme", "paper");
        string? last = null;
        store.Changed += (_, e) => last = e.Value;

        Assert.True(store.TrySetValue("client.theme", null));

        Assert.Equal("void", store.GetValue("client.theme"));
        Assert.Equal("void", last);
    }

    [Fact]
    public void Values_survive_a_restart_through_the_json_file()
    {
        var first = new ClientSettingsStore(_definitions, FilePath);
        first.TrySetValue("client.theme", "paper");
        first.TrySetValue("client.motion_level", "reduced");

        var second = new ClientSettingsStore(_definitions, FilePath);

        Assert.Equal("paper", second.GetValue("client.theme"));
        Assert.Equal("reduced", second.GetValue("client.motion_level"));
    }

    [Fact]
    public void A_corrupt_file_gives_defaults_and_is_reported()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");
        var log = new List<string>();

        var store = new ClientSettingsStore(_definitions, FilePath, log.Add);

        Assert.Equal("void", store.GetValue("client.theme"));
        Assert.Single(log);
    }

    [Fact]
    public void Stored_values_the_definitions_no_longer_accept_are_dropped_on_load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{"client.theme":"neon","client.removed":"x","client.motion_level":"subtle"}""");

        var store = new ClientSettingsStore(_definitions, FilePath);

        Assert.Equal("void", store.GetValue("client.theme"));
        Assert.Equal("subtle", store.GetValue("client.motion_level"));
    }

    [Fact]
    public void A_failed_save_keeps_the_value_for_this_run_and_logs()
    {
        // The settings path is a directory, so replacing it with a file fails.
        string blocked = Path.Combine(_dir, "blocked.json");
        Directory.CreateDirectory(Path.Combine(blocked, "child"));
        var log = new List<string>();
        var store = new ClientSettingsStore(_definitions, blocked, log.Add);

        Assert.True(store.TrySetValue("client.theme", "paper"));

        Assert.Equal("paper", store.GetValue("client.theme"));
        Assert.Single(log);
    }

    [Theory]
    [InlineData("compact", ClientDensity.Compact)]
    [InlineData("COMPACT", ClientDensity.Compact)]
    [InlineData("comfortable", ClientDensity.Comfortable)]
    [InlineData("tiny", ClientDensity.Comfortable)]
    [InlineData(null, ClientDensity.Comfortable)]
    public void Density_parses_with_a_comfortable_fallback(string? value, ClientDensity expected) =>
        Assert.Equal(expected, ClientDensitySettings.Parse(value));
}
