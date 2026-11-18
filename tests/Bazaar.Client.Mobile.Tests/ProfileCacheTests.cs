using System.Text.Json;
using Bazaar.Client.Mobile;

namespace Bazaar.Client.Mobile.Tests;

public sealed class ProfileCacheTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-profile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Memory_cache_is_empty_before_the_first_save()
    {
        var cache = new MemoryProfileCache(new FixedClock(_now));

        Assert.Null(cache.Load());
    }

    [Fact]
    public void Memory_cache_round_trips_the_profile_and_the_save_time()
    {
        var cache = new MemoryProfileCache(new FixedClock(_now));

        cache.Save(new PhoneProfile(7, ["gc.list", "kanban"]));
        var loaded = cache.Load();

        Assert.NotNull(loaded);
        Assert.Equal(7, loaded.Profile.Revision);
        Assert.Equal(["gc.list", "kanban"], loaded.Profile.PaneKinds.ToArray());
        Assert.Equal(_now, loaded.SavedAt);
    }

    [Fact]
    public void Memory_cache_keeps_its_own_copy_of_the_pane_list()
    {
        var cache = new MemoryProfileCache(new FixedClock(_now));
        var kinds = new List<string> { "a" };
        cache.Save(new PhoneProfile(1, kinds));

        kinds.Add("b");

        Assert.Equal(["a"], cache.Load()!.Profile.PaneKinds.ToArray());
    }

    [Fact]
    public void File_cache_round_trips_across_instances()
    {
        new FileProfileCache(_dir, new FixedClock(_now)).Save(new PhoneProfile(12, ["gc.list", "kanban"]));

        var loaded = new FileProfileCache(_dir).Load();

        Assert.NotNull(loaded);
        Assert.Equal(12, loaded.Profile.Revision);
        Assert.Equal(["gc.list", "kanban"], loaded.Profile.PaneKinds.ToArray());
        Assert.Equal(_now, loaded.SavedAt);
    }

    [Fact]
    public void Missing_file_returns_no_cache()
    {
        var cache = new FileProfileCache(_dir);

        Assert.Null(cache.Load());
    }

    [Theory]
    [InlineData("{ \"revision\": 1, \"savedAt\": \"2026-11-15T09:00:00+00:00\", \"profile\": { \"paneKinds\": [ \"a\", ")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"revision\": 1, \"savedAt\": \"2026-11-15T09:00:00+00:00\" }")]
    [InlineData("{ \"revision\": 1, \"savedAt\": \"2026-11-15T09:00:00+00:00\", \"profile\": { \"paneKinds\": [ null ] } }")]
    [InlineData("{ \"revision\": -1, \"savedAt\": \"2026-11-15T09:00:00+00:00\", \"profile\": { \"paneKinds\": [] } }")]
    [InlineData("{ \"savedAt\": \"2026-11-15T09:00:00+00:00\", \"profile\": { \"paneKinds\": [] } }")]
    public void Corrupt_or_incomplete_file_returns_no_cache_and_is_discarded(string contents)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, FileProfileCache.FileName);
        File.WriteAllText(path, contents);

        var cache = new FileProfileCache(_dir);

        Assert.Null(cache.Load());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Save_replaces_the_previous_profile_and_leaves_no_temp_file()
    {
        var cache = new FileProfileCache(_dir, new FixedClock(_now));
        cache.Save(new PhoneProfile(1, ["old"]));

        cache.Save(new PhoneProfile(2, ["new"]));

        Assert.Equal(2, new FileProfileCache(_dir).Load()!.Profile.Revision);
        Assert.False(File.Exists(Path.Combine(_dir, FileProfileCache.FileName + ".tmp")));
    }

    [Fact]
    public void A_stale_temp_file_from_an_interrupted_write_is_ignored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, FileProfileCache.FileName + ".tmp"), "{ half written");
        var cache = new FileProfileCache(_dir);

        Assert.Null(cache.Load());

        cache.Save(new PhoneProfile(3, ["kanban"]));

        Assert.Equal(3, cache.Load()!.Profile.Revision);
    }

    [Fact]
    public void Save_creates_a_missing_directory()
    {
        var nested = Path.Combine(_dir, "nested", "android");

        new FileProfileCache(nested).Save(new PhoneProfile(1, ["a"]));

        Assert.True(File.Exists(Path.Combine(nested, FileProfileCache.FileName)));
    }

    [Fact]
    public void Stored_file_holds_only_profile_fields_and_no_bodies()
    {
        var cache = new FileProfileCache(_dir, new FixedClock(_now));
        cache.Save(new PhoneProfile(5, ["gc.list", "kanban"]));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, FileProfileCache.FileName)));
        var root = doc.RootElement;

        // The top level holds the revision, the save time and the profile, and nothing else.
        Assert.Equal(["profile", "revision", "savedAt"], root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["paneKinds"], root.GetProperty("profile").EnumerateObject().Select(p => p.Name).ToArray());

        // Every string in the file is a pane kind or the save time. A message body would show up here.
        var expected = new[] { "gc.list", "kanban", root.GetProperty("savedAt").GetString()! };
        Assert.Equal(expected.Order(), AllStrings(root).Order());
    }

    [Fact]
    public void Constructor_rejects_an_empty_directory()
    {
        Assert.Throws<ArgumentException>(() => new FileProfileCache(" "));
    }

    private static IEnumerable<string> AllStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var s in AllStrings(item))
                    {
                        yield return s;
                    }
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var s in AllStrings(property.Value))
                    {
                        yield return s;
                    }
                }

                break;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
