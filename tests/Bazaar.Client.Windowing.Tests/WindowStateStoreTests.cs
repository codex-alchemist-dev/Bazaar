using System.Text.Json;

namespace Bazaar.Client.Windowing.Tests;

/// <summary>Save and load of the window list. Load must never throw and must keep only valid, bounded state.</summary>
public sealed class WindowStateStoreTests
{
    // One valid entry. The replacement tests below edit this text, so the template must stay in step with Save.
    private const string _validEntry =
        "{\"id\":\"w1\",\"kind\":\"Detached\",\"title\":\"t\",\"bounds\":{\"x\":100,\"y\":100,\"width\":400,\"height\":300},"
        + "\"state\":\"Normal\",\"monitorId\":\"primary\",\"path\":null,\"layout\":null}";

    private static (WindowStateStore Store, FakeWindowStateFile File) Create(string? text = null, IOException? readFailure = null)
    {
        var file = new FakeWindowStateFile { Text = text, ReadFailure = readFailure };
        var store = new WindowStateStore(file, new FakeMonitorSource(TestData.Primary, TestData.Right));
        return (store, file);
    }

    private static string Document(string entries) => "{\"v\":1,\"windows\":[" + entries + "]}";

    [Fact]
    public void Round_trip_keeps_every_field_of_on_screen_windows()
    {
        var (store, _) = Create();
        var windows = new List<WindowRecord>
        {
            TestData.Window("main", WindowKind.Main, "Shell", new WindowBounds(0, 0, 1200, 800), WindowState.Maximized, "primary"),
            TestData.Window("pane", WindowKind.Detached, "Quote \"x\" - \u00fcber", new WindowBounds(2000, 100, 800, 600), WindowState.Normal, "right", "C:/work/file.md"),
            TestData.Window("editor", WindowKind.Editor, "Editor", new WindowBounds(10, 10, 500, 400), WindowState.Normal, null, null),
        };

        store.Save(windows);
        var loaded = store.Load();

        Assert.Equal(windows, loaded);
    }

    [Fact]
    public void Save_writes_version_one_first()
    {
        var (store, file) = Create();

        store.Save([TestData.Window("w1")]);

        Assert.StartsWith("{\"v\":1,", file.Text);
        Assert.Equal(1, file.WriteCount);
    }

    [Fact]
    public void Save_writes_only_the_contract_fields()
    {
        var (store, file) = Create();

        store.Save([TestData.Window("w1", path: "a.md")]);

        using var document = JsonDocument.Parse(file.Text!);
        var entry = document.RootElement.GetProperty("windows")[0];
        var names = entry.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["id", "kind", "title", "bounds", "state", "monitorId", "path", "layout"], names);
        Assert.Equal("Detached", entry.GetProperty("kind").GetString());
    }

    [Fact]
    public void Empty_list_round_trips_to_empty()
    {
        var (store, _) = Create();

        store.Save([]);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void Missing_file_loads_as_empty()
    {
        var (store, _) = Create(text: null);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void Read_failure_loads_as_empty()
    {
        var (store, _) = Create(readFailure: new IOException("disk gone"));

        Assert.Empty(store.Load());
    }

    [Fact]
    public void Unauthorized_read_loads_as_empty()
    {
        var file = new FakeWindowStateFile { ReadFailure = new UnauthorizedAccessException("denied") };
        var store = new WindowStateStore(file, new FakeMonitorSource(TestData.Primary));

        Assert.Empty(store.Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"v\":1}")]
    [InlineData("{\"v\":2,\"windows\":[]}")]
    [InlineData("{\"v\":0,\"windows\":[]}")]
    [InlineData("{\"v\":\"1\",\"windows\":[]}")]
    [InlineData("{\"v\":1.5,\"windows\":[]}")]
    [InlineData("{\"v\":1,\"windows\":{}}")]
    [InlineData("{\"v\":1,\"windows\":\"nope\"}")]
    public void Corrupt_or_unknown_documents_load_as_empty(string text)
    {
        var (store, _) = Create(text);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void Unknown_version_loads_as_empty_even_when_entries_look_valid()
    {
        var (store, _) = Create("{\"v\":2,\"windows\":[" + _validEntry + "]}");

        Assert.Empty(store.Load());
    }

    [Theory]
    [InlineData("\"kind\":\"Detached\"", "\"kind\":\"Browser\"")]
    [InlineData("\"kind\":\"Detached\"", "\"kind\":\"detached\"")]
    [InlineData("\"kind\":\"Detached\"", "\"kind\":7")]
    [InlineData("\"state\":\"Normal\"", "\"state\":\"Fullscreen\"")]
    [InlineData("\"id\":\"w1\"", "\"id\":\"\"")]
    [InlineData("\"id\":\"w1\",", "")]
    [InlineData("\"title\":\"t\",", "")]
    [InlineData("\"title\":\"t\"", "\"title\":5")]
    [InlineData("\"bounds\":{\"x\":100,\"y\":100,\"width\":400,\"height\":300},", "")]
    [InlineData("\"width\":400", "\"width\":\"400\"")]
    [InlineData("\"width\":400", "\"width\":1.5")]
    [InlineData("\"width\":400", "\"width\":99999999999")]
    [InlineData("\"monitorId\":\"primary\",", "")]
    [InlineData("\"path\":null,", "")]
    [InlineData(",\"layout\":null", "")]
    public void Entry_with_a_bad_field_is_skipped_and_the_good_entry_is_kept(string find, string replace)
    {
        var edited = _validEntry.Replace(find, replace, StringComparison.Ordinal);
        Assert.NotEqual(_validEntry, edited);
        var bad = edited.Replace("\"w1\"", "\"bad\"", StringComparison.Ordinal);
        var good = _validEntry.Replace("\"w1\"", "\"good\"", StringComparison.Ordinal);
        var (store, _) = Create(Document(bad + "," + good));

        var loaded = store.Load();

        Assert.Equal(["good"], loaded.Select(w => w.Id.Value));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void Non_object_entry_is_skipped(string entry)
    {
        var (store, _) = Create(Document(entry + "," + _validEntry));

        Assert.Single(store.Load());
    }

    [Fact]
    public void Duplicate_ids_keep_the_first()
    {
        var (store, _) = Create();
        store.Save([TestData.Window("dup", title: "first"), TestData.Window("dup", title: "second")]);

        var loaded = store.Load();

        Assert.Equal(["first"], loaded.Select(w => w.Title));
    }

    [Fact]
    public void A_skipped_entry_does_not_claim_its_id()
    {
        // The first "w1" is invalid (unknown kind), so the second "w1" is the first valid one and is kept.
        var invalid = _validEntry.Replace("\"Detached\"", "\"Nope\"", StringComparison.Ordinal);
        var (store, _) = Create(Document(invalid + "," + _validEntry.Replace("\"title\":\"t\"", "\"title\":\"second\"", StringComparison.Ordinal)));

        var loaded = store.Load();

        Assert.Equal(["second"], loaded.Select(w => w.Title));
    }

    [Fact]
    public void Only_one_main_window_is_kept()
    {
        var (store, _) = Create();
        store.Save(
        [
            TestData.Window("m1", WindowKind.Main, "first main"),
            TestData.Window("p1", WindowKind.Detached, "pane"),
            TestData.Window("m2", WindowKind.Main, "second main"),
            TestData.Window("m3", WindowKind.Main, "third main"),
        ]);

        var loaded = store.Load();

        Assert.Equal(["m1", "p1"], loaded.Select(w => w.Id.Value));
        Assert.Equal("first main", loaded[0].Title);
    }

    [Fact]
    public void An_invalid_main_entry_does_not_take_the_main_slot()
    {
        var invalidMain = _validEntry.Replace("\"Detached\"", "\"Main\"", StringComparison.Ordinal).Replace("\"Normal\"", "\"Fullscreen\"", StringComparison.Ordinal);
        var validMain = _validEntry.Replace("\"Detached\"", "\"Main\"", StringComparison.Ordinal).Replace("\"w1\"", "\"m2\"", StringComparison.Ordinal);
        var (store, _) = Create(Document(invalidMain + "," + validMain));

        var loaded = store.Load();

        Assert.Equal(["m2"], loaded.Select(w => w.Id.Value));
        Assert.Equal(WindowKind.Main, loaded[0].Kind);
    }

    [Fact]
    public void At_most_32_windows_are_loaded_in_file_order()
    {
        var windows = Enumerable.Range(0, 40).Select(i => TestData.Window($"w{i}", title: $"t{i}")).ToList();
        var (store, _) = Create();
        store.Save(windows);

        var loaded = store.Load();

        Assert.Equal(WindowStateStore.MaxWindows, loaded.Count);
        Assert.Equal("w0", loaded[0].Id.Value);
        Assert.Equal("w31", loaded[31].Id.Value);
    }

    [Fact]
    public void The_cap_counts_only_valid_entries()
    {
        var invalid = Enumerable.Repeat("42", 3);
        var valid = Enumerable.Range(0, 40).Select(i => _validEntry.Replace("\"w1\"", $"\"v{i}\"", StringComparison.Ordinal));
        var (store, _) = Create(Document(string.Join(",", invalid.Concat(valid))));

        var loaded = store.Load();

        Assert.Equal(WindowStateStore.MaxWindows, loaded.Count);
        Assert.Equal("v0", loaded[0].Id.Value);
    }

    [Fact]
    public void Minimized_windows_load_as_normal()
    {
        var (store, _) = Create();
        store.Save([TestData.Window("min", state: WindowState.Minimized), TestData.Window("max", state: WindowState.Maximized)]);

        var loaded = store.Load();

        Assert.Equal(WindowState.Normal, loaded[0].State);
        Assert.Equal(WindowState.Maximized, loaded[1].State);
    }

    [Fact]
    public void Saved_bounds_are_fitted_to_their_monitor_on_load()
    {
        var (store, _) = Create();
        store.Save([TestData.Window("w1", bounds: new WindowBounds(5000, 5000, 800, 600), monitorId: "primary")]);

        var loaded = store.Load();

        Assert.Equal(new WindowBounds(1120, 440, 800, 600), loaded[0].Bounds);
    }

    [Fact]
    public void Window_on_a_removed_monitor_is_fitted_to_the_monitor_it_overlaps()
    {
        var (store, _) = Create();
        var wanted = new WindowBounds(2100, 100, 800, 600);
        store.Save([TestData.Window("w1", bounds: wanted, monitorId: "unplugged")]);

        var loaded = store.Load();

        Assert.Equal(wanted, loaded[0].Bounds);
    }

    [Fact]
    public void Unknown_fields_are_neither_loaded_nor_saved_again()
    {
        var entry = _validEntry.Replace("\"path\":null", "\"path\":null,\"secret\":\"token-123\"", StringComparison.Ordinal);
        var (store, file) = Create(Document(entry).Replace("\"v\":1", "\"v\":1,\"extra\":1", StringComparison.Ordinal));

        var loaded = store.Load();
        store.Save(loaded);

        Assert.Single(loaded);
        Assert.DoesNotContain("secret", file.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("extra", file.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("token-123", file.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_is_read_through_the_dock_codec_when_present()
    {
        // DockJson is a stub in this branch, so only the non-null path is exercised: the entry still loads.
        var entry = _validEntry.Replace("\"layout\":null", "\"layout\":{\"pane\":\"x\"}", StringComparison.Ordinal);
        var (store, _) = Create(Document(entry));

        Assert.Single(store.Load());
    }
}
