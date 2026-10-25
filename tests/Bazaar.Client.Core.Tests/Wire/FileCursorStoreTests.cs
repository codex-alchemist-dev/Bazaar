using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Tests.Wire;

public sealed class FileCursorStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-cursors-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public FileCursorStoreTests()
    {
        _path = Path.Combine(_dir, "cursors.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Cursors_persist_across_instances()
    {
        new FileCursorStore(_path).Save(new StreamCursor("gc:a", 11, 39));

        var reopened = new FileCursorStore(_path);

        Assert.Equal(new StreamCursor("gc:a", 11, 39), reopened.Load("gc:a"));
    }

    [Fact]
    public void Unknown_stream_returns_zeros()
    {
        var store = new FileCursorStore(_path);

        Assert.Equal(new StreamCursor("gc:none", 0, 0), store.Load("gc:none"));
    }

    [Fact]
    public void Missing_file_starts_empty_and_is_not_created_by_load()
    {
        var store = new FileCursorStore(_path);

        Assert.Equal(new StreamCursor("gc:a", 0, 0), store.Load("gc:a"));
        Assert.False(File.Exists(_path));
        Assert.Null(store.LastWriteError);
    }

    [Fact]
    public void Last_write_wins()
    {
        var store = new FileCursorStore(_path);

        store.Save(new StreamCursor("gc:a", 5, 9));
        store.Save(new StreamCursor("gc:a", 3, 20));

        Assert.Equal(new StreamCursor("gc:a", 3, 20), store.Load("gc:a"));
        Assert.Equal(new StreamCursor("gc:a", 3, 20), new FileCursorStore(_path).Load("gc:a"));
    }

    [Fact]
    public void Multiple_streams_are_stored_independently()
    {
        var store = new FileCursorStore(_path);

        store.Save(new StreamCursor("gc:a", 1, 2));
        store.Save(new StreamCursor("gc:b", 3, 4));
        store.Save(new StreamCursor("gc:c", 5, 6));

        var reopened = new FileCursorStore(_path);
        Assert.Equal(new StreamCursor("gc:a", 1, 2), reopened.Load("gc:a"));
        Assert.Equal(new StreamCursor("gc:b", 3, 4), reopened.Load("gc:b"));
        Assert.Equal(new StreamCursor("gc:c", 5, 6), reopened.Load("gc:c"));
    }

    [Fact]
    public void Corrupt_json_starts_empty_is_quarantined_and_a_later_save_repairs_the_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, "{\"version\":1,\"cursors\":[{\"s\":");

        var store = new FileCursorStore(_path);

        Assert.Equal(new StreamCursor("gc:a", 0, 0), store.Load("gc:a"));
        Assert.False(File.Exists(_path));
        Assert.True(File.Exists(_path + ".corrupt"));

        store.Save(new StreamCursor("gc:a", 7, 8));

        Assert.True(File.Exists(_path));
        Assert.Equal(new StreamCursor("gc:a", 7, 8), new FileCursorStore(_path).Load("gc:a"));
    }

    [Fact]
    public void Empty_file_starts_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, string.Empty);

        var store = new FileCursorStore(_path);

        Assert.Equal(new StreamCursor("gc:a", 0, 0), store.Load("gc:a"));
        Assert.True(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void Unknown_version_starts_empty_and_is_quarantined()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, "{\"version\":99,\"cursors\":[{\"s\":\"gc:a\",\"after\":1,\"editAfter\":1}]}");

        var store = new FileCursorStore(_path);

        Assert.Equal(new StreamCursor("gc:a", 0, 0), store.Load("gc:a"));
        Assert.True(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void Missing_parent_directory_is_created_on_save()
    {
        var nested = Path.Combine(_dir, "a", "b", "cursors.json");
        var store = new FileCursorStore(nested);

        store.Save(new StreamCursor("gc:a", 2, 3));

        Assert.True(File.Exists(nested));
        Assert.Equal(new StreamCursor("gc:a", 2, 3), new FileCursorStore(nested).Load("gc:a"));
    }

    [Fact]
    public void No_temp_files_are_left_after_saves()
    {
        var store = new FileCursorStore(_path);

        for (var i = 1; i <= 5; i++)
        {
            store.Save(new StreamCursor("gc:a", i, i));
        }

        var names = Directory.GetFiles(_dir).Select(Path.GetFileName).ToList();
        Assert.Equal(["cursors.json"], names);
    }

    [Fact]
    public void Stale_temp_file_does_not_affect_loading()
    {
        new FileCursorStore(_path).Save(new StreamCursor("gc:a", 4, 5));
        File.WriteAllText(_path + ".tmp", "{\"version\":1,\"cursors\":[{\"s\":\"gc:a\",\"after\":999,\"editAfter\":999}]");

        var store = new FileCursorStore(_path);

        Assert.Equal(new StreamCursor("gc:a", 4, 5), store.Load("gc:a"));
        Assert.False(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public async Task Concurrent_saves_leave_a_valid_file_matching_the_final_state()
    {
        var store = new FileCursorStore(_path);
        var tasks = Enumerable.Range(0, 50).Select(t => Task.Run(() =>
        {
            for (var i = 1; i <= 20; i++)
            {
                store.Save(new StreamCursor($"gc:{t % 10}", t * 100 + i, i));
            }
        }, TestContext.Current.CancellationToken)).ToList();

        await Task.WhenAll(tasks);

        Assert.False(File.Exists(_path + ".corrupt"));
        var reopened = new FileCursorStore(_path);
        for (var s = 0; s < 10; s++)
        {
            var stream = $"gc:{s}";
            Assert.Equal(store.Load(stream), reopened.Load(stream));
        }
        Assert.Null(store.LastWriteError);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path,
            "{\"version\":1,\"extra\":true,\"cursors\":[{\"s\":\"gc:a\",\"after\":6,\"editAfter\":7,\"note\":\"x\"}]}");

        var store = new FileCursorStore(_path);

        Assert.Equal(new StreamCursor("gc:a", 6, 7), store.Load("gc:a"));
        Assert.False(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void Write_failure_does_not_throw_and_sets_LastWriteError()
    {
        // A directory at the cursor path makes the final move fail.
        Directory.CreateDirectory(_path);
        var store = new FileCursorStore(_path);

        store.Save(new StreamCursor("gc:a", 1, 1));

        Assert.NotNull(store.LastWriteError);
        Assert.Equal(new StreamCursor("gc:a", 1, 1), store.Load("gc:a"));
        Assert.False(File.Exists(_path + ".tmp"));
    }
}
