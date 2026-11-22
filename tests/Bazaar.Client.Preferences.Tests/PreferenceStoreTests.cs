using Bazaar.Client.Preferences.Abstractions;

namespace Bazaar.Client.Preferences.Tests;

public sealed class PreferenceStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("prefs").FullName;

    private static readonly PreferenceDefinition[] _defs =
    [
        new("ui.hints", PreferenceKind.Bool, "true"),
        new("ui.page", PreferenceKind.WholeNumber, "20", Min: 5, Max: 100),
        new("ui.density", PreferenceKind.Choice, "cozy", Choices: ["cozy", "compact"]),
        new("ui.name", PreferenceKind.Text, "", Max: 8),
    ];

    private sealed class MemoryStorage : IPreferenceStorage
    {
        public PreferenceSnapshot Snapshot { get; set; } = PreferenceSnapshot.Empty;

        public PreferenceSnapshot Load() => Snapshot;

        public void Save(PreferenceSnapshot snapshot) => Snapshot = snapshot;
    }

    private sealed class Sync : IPreferenceSync
    {
        public List<(string Key, string Value)> Sent { get; } = [];

        public bool Fail { get; set; }

        public Task PushAsync(string key, string value, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new IOException("offline");
            }

            Sent.Add((key, value));
            return Task.CompletedTask;
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Defaults_come_from_the_registry_and_a_set_is_saved_and_raises_changed()
    {
        var storage = new MemoryStorage();
        var store = new PreferenceStore(_defs, storage);
        var changed = new List<string>();
        store.Changed += changed.Add;

        Assert.True(store.GetBool("ui.hints"));
        Assert.Equal(20, store.GetInt("ui.page"));
        Assert.True(store.TrySet("ui.page", "30").Accepted);

        Assert.Equal(30, store.GetInt("ui.page"));
        Assert.Equal(["ui.page"], changed);
        Assert.Equal("30", storage.Snapshot.Values["ui.page"]);
        Assert.Equal(["ui.page"], storage.Snapshot.Pending);
    }

    [Theory]
    [InlineData("ui.page", "4")]
    [InlineData("ui.page", "101")]
    [InlineData("ui.page", "abc")]
    [InlineData("ui.hints", "maybe")]
    [InlineData("ui.density", "huge")]
    [InlineData("ui.name", "123456789")]
    public void An_invalid_value_is_refused_and_changes_nothing(string key, string value)
    {
        var store = new PreferenceStore(_defs, new MemoryStorage());
        string? before = store.GetValue(key);

        PreferenceResult result = store.TrySet(key, value);

        Assert.Equal(PreferenceRefusal.InvalidValue, result.Refusal);
        Assert.Equal(before, store.GetValue(key));
        Assert.Empty(store.Pending);
    }

    [Fact]
    public void An_undeclared_key_is_refused_and_reads_as_null()
    {
        var store = new PreferenceStore(_defs, new MemoryStorage());

        Assert.Equal(PreferenceRefusal.UnknownKey, store.TrySet("nope", "1").Refusal);
        Assert.Null(store.GetValue("nope"));
        Assert.False(store.IsDeclared("nope"));
    }

    [Fact]
    public void Bool_and_int_values_are_stored_in_canonical_form()
    {
        var store = new PreferenceStore(_defs, new MemoryStorage());

        store.TrySet("ui.hints", "FALSE");
        store.TrySet("ui.page", "+042");

        Assert.Equal("false", store.GetValue("ui.hints"));
        Assert.Equal("42", store.GetValue("ui.page"));
    }

    [Fact]
    public void Stored_values_that_no_longer_validate_fall_back_to_the_default_on_load()
    {
        var storage = new MemoryStorage
        {
            Snapshot = new PreferenceSnapshot(
                new Dictionary<string, string> { ["ui.page"] = "999", ["gone.key"] = "x", ["ui.density"] = "compact" },
                ["ui.page", "ui.density"]),
        };

        var store = new PreferenceStore(_defs, storage);

        Assert.Equal(20, store.GetInt("ui.page"));
        Assert.Equal("compact", store.GetValue("ui.density"));
        Assert.Equal(["ui.density"], store.Pending);
    }

    [Fact]
    public void A_duplicate_definition_or_a_bad_default_is_refused_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new PreferenceStore([.. _defs, _defs[0]], new MemoryStorage()));
        Assert.Throws<ArgumentException>(() => new PreferenceStore([new("k", PreferenceKind.WholeNumber, "x")], new MemoryStorage()));
    }

    [Fact]
    public async Task Flush_pushes_in_order_and_clears_pending()
    {
        var sync = new Sync();
        var store = new PreferenceStore(_defs, new MemoryStorage(), sync);
        store.TrySet("ui.page", "30");
        store.TrySet("ui.hints", "false");

        int sent = await store.FlushAsync(CancellationToken.None);

        Assert.Equal(2, sent);
        Assert.Equal([("ui.page", "30"), ("ui.hints", "false")], sync.Sent);
        Assert.Empty(store.Pending);
    }

    [Fact]
    public async Task A_failed_push_keeps_the_change_pending_and_a_later_flush_sends_it()
    {
        var sync = new Sync { Fail = true };
        var store = new PreferenceStore(_defs, new MemoryStorage(), sync);
        store.TrySet("ui.page", "30");

        Assert.Equal(0, await store.FlushAsync(CancellationToken.None));
        Assert.Equal(["ui.page"], store.Pending);

        sync.Fail = false;
        Assert.Equal(1, await store.FlushAsync(CancellationToken.None));
        Assert.Empty(store.Pending);
    }

    [Fact]
    public void A_server_update_applies_unless_a_local_change_is_still_pending()
    {
        var store = new PreferenceStore(_defs, new MemoryStorage());

        store.ApplyFromServer("ui.density", "compact");
        Assert.Equal("compact", store.GetValue("ui.density"));
        Assert.Empty(store.Pending);

        store.TrySet("ui.page", "30");
        store.ApplyFromServer("ui.page", "50");
        Assert.Equal(30, store.GetInt("ui.page"));
    }

    [Fact]
    public void The_file_storage_round_trips_and_survives_a_corrupt_file()
    {
        string path = Path.Combine(_dir, "sub", "prefs.json");
        var reports = new List<string>();
        var first = new PreferenceStore(_defs, new FilePreferenceStorage(path, reports.Add));
        first.TrySet("ui.density", "compact");

        var second = new PreferenceStore(_defs, new FilePreferenceStorage(path, reports.Add));
        Assert.Equal("compact", second.GetValue("ui.density"));
        Assert.Equal(["ui.density"], second.Pending);

        File.WriteAllText(path, "{ not json");
        var third = new PreferenceStore(_defs, new FilePreferenceStorage(path, reports.Add));
        Assert.Equal("cozy", third.GetValue("ui.density"));
        Assert.True(File.Exists(path + ".bad"));
        Assert.Single(reports);
    }
}
