using System.Security.Cryptography;
using Bazaar.Server.Components.Backup.Restore;

namespace Bazaar.Server.Components.Backup.Tests.Restore;

public sealed class FileRestoreReadOnlyStateStoreTests
{
    [Fact]
    public async Task Persist_then_read_round_trips_and_sets_read_only()
    {
        using var dir = new TempDir();
        var store = new FileRestoreReadOnlyStateStore(Path.Combine(dir.Path, "sub", "ro.json"));
        Assert.False(store.IsReadOnly());
        Assert.Null(await store.ReadRestoreIdAsync(TestContext.Current.CancellationToken));

        await store.PersistAsync("r1", TestContext.Current.CancellationToken);

        Assert.True(store.IsReadOnly());
        Assert.Equal("r1", await store.ReadRestoreIdAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "sub"), "*.tmp"));
    }

    [Fact]
    public async Task Crash_between_temp_write_and_rename_keeps_old_state()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "ro.json");
        await new FileRestoreReadOnlyStateStore(path).PersistAsync("old", TestContext.Current.CancellationToken);

        var crashing = new FileRestoreReadOnlyStateStore(path, _ => throw new IOException("crash"));
        await Assert.ThrowsAsync<IOException>(() => crashing.PersistAsync("new", TestContext.Current.CancellationToken));

        Assert.Equal("old", await new FileRestoreReadOnlyStateStore(path).ReadRestoreIdAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public async Task Crash_on_first_persist_leaves_no_record_and_throws()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "ro.json");
        var crashing = new FileRestoreReadOnlyStateStore(path, _ => throw new IOException("crash"));

        await Assert.ThrowsAsync<IOException>(() => crashing.PersistAsync("r1", TestContext.Current.CancellationToken));

        Assert.False(crashing.IsReadOnly());
    }

    [Fact]
    public async Task Clear_removes_matching_record_and_ignores_missing()
    {
        using var dir = new TempDir();
        var store = new FileRestoreReadOnlyStateStore(Path.Combine(dir.Path, "ro.json"));
        await store.PersistAsync("r1", TestContext.Current.CancellationToken);

        await store.ClearAsync("r1", TestContext.Current.CancellationToken);
        await store.ClearAsync("r1", TestContext.Current.CancellationToken);

        Assert.False(store.IsReadOnly());
    }

    [Fact]
    public async Task Clear_refuses_a_record_of_another_restore()
    {
        using var dir = new TempDir();
        var store = new FileRestoreReadOnlyStateStore(Path.Combine(dir.Path, "ro.json"));
        await store.PersistAsync("r1", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClearAsync("r2", TestContext.Current.CancellationToken));

        Assert.True(store.IsReadOnly());
    }

    [Fact]
    public async Task Corrupt_record_fails_closed()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "ro.json");
        await File.WriteAllTextAsync(path, "{not json", TestContext.Current.CancellationToken);
        var store = new FileRestoreReadOnlyStateStore(path);

        Assert.True(store.IsReadOnly());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadRestoreIdAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ClearAsync("r1", TestContext.Current.CancellationToken));
        Assert.True(store.IsReadOnly());
    }

    [Fact]
    public async Task Blank_restore_id_is_rejected()
    {
        using var dir = new TempDir();
        var store = new FileRestoreReadOnlyStateStore(Path.Combine(dir.Path, "ro.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PersistAsync(" ", TestContext.Current.CancellationToken));
    }
}

public sealed class DirectoryLiveDataPreserverTests
{
    private static readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 18, 10, 30, 5, TimeSpan.Zero));

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Make(TempDir dir, string name, string file = "a.txt")
    {
        var d = Path.Combine(dir.Path, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, file), name);
        return d;
    }

    [Fact]
    public async Task Moves_each_directory_to_a_timestamped_sibling_and_keeps_contents()
    {
        using var dir = new TempDir();
        var souls = Make(dir, "souls");
        var vault = Make(dir, "vault");
        var p = new DirectoryLiveDataPreserver([souls, vault], _time);

        await p.PreserveAsync("r1", TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(souls));
        Assert.False(Directory.Exists(vault));
        var moved = Path.Combine(dir.Path, "souls.preserved-20261118T103005Z-r1");
        Assert.Equal("souls", File.ReadAllText(Path.Combine(moved, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "vault.preserved-20261118T103005Z-r1", "a.txt")));
    }

    [Fact]
    public async Task Existing_target_is_refused_and_nothing_moves()
    {
        using var dir = new TempDir();
        var souls = Make(dir, "souls");
        var vault = Make(dir, "vault");
        Directory.CreateDirectory(Path.Combine(dir.Path, "vault.preserved-20261118T103005Z-r1"));
        var p = new DirectoryLiveDataPreserver([souls, vault], _time);

        await Assert.ThrowsAsync<IOException>(() => p.PreserveAsync("r1", TestContext.Current.CancellationToken));

        Assert.True(File.Exists(Path.Combine(souls, "a.txt")));
        Assert.True(File.Exists(Path.Combine(vault, "a.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(dir.Path, "vault.preserved-20261118T103005Z-r1")));
    }

    [Fact]
    public async Task Missing_directories_are_skipped()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var p = new DirectoryLiveDataPreserver([Path.Combine(dir.Path, "none")], _time);

        await p.PreserveAsync("r1", TestContext.Current.CancellationToken);

        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Nested_directories_are_refused_before_any_move()
    {
        using var dir = new TempDir();
        var outer = Make(dir, "outer");
        var inner = Make(dir, Path.Combine("outer", "inner"));
        var p = new DirectoryLiveDataPreserver([outer, inner], _time);

        await Assert.ThrowsAsync<IOException>(() => p.PreserveAsync("r1", TestContext.Current.CancellationToken));

        Assert.True(Directory.Exists(inner));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a b")]
    public async Task Unsafe_restore_ids_are_rejected(string id)
    {
        using var dir = new TempDir();
        var p = new DirectoryLiveDataPreserver([Make(dir, "souls")], _time);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => p.PreserveAsync(id, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "souls")));
    }

    [Fact]
    public void No_directories_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new DirectoryLiveDataPreserver([]));
}

public sealed class BackupSetDigestTests
{
    [Fact]
    public async Task Describes_size_and_lowercase_sha256()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "set.bin");
        byte[] data = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);

        var d = await BackupSetDigest.DescribeFileAsync(path, TestContext.Current.CancellationToken);

        Assert.NotNull(d);
        Assert.Equal(5, d.Value.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), d.Value.Sha256Hex);
    }

    [Fact]
    public async Task Missing_file_returns_null()
    {
        using var dir = new TempDir();
        Assert.Null(await BackupSetDigest.DescribeFileAsync(Path.Combine(dir.Path, "nope"), TestContext.Current.CancellationToken));
    }
}
