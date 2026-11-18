using Bazaar.Server.Backup.Archiving;

namespace Bazaar.Server.Backup.Tests;

public sealed class WalArchiverTests
{
    [Fact]
    public async Task Archives_a_copy_that_matches_the_source()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "000000010000000000000001");
        Directory.CreateDirectory(dir.Path);
        File.WriteAllBytes(src, [1, 2, 3, 4]);
        var archive = Path.Combine(dir.Path, "archive");

        await WalArchiver.ArchiveAsync(src, archive, "000000010000000000000001", TestContext.Current.CancellationToken);

        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Path.Combine(archive, "000000010000000000000001")));
        Assert.Empty(Directory.EnumerateFiles(archive, "*.partial-*"));
    }

    [Fact]
    public async Task Identical_existing_file_is_success()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var src = Path.Combine(dir.Path, "seg");
        File.WriteAllBytes(src, [9, 9]);
        var archive = Path.Combine(dir.Path, "archive");
        await WalArchiver.ArchiveAsync(src, archive, "seg", TestContext.Current.CancellationToken);

        await WalArchiver.ArchiveAsync(src, archive, "seg", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Different_existing_file_is_a_conflict_and_is_not_overwritten()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var src = Path.Combine(dir.Path, "seg");
        var archive = Path.Combine(dir.Path, "archive");
        Directory.CreateDirectory(archive);
        File.WriteAllBytes(Path.Combine(archive, "seg"), [1]);
        File.WriteAllBytes(src, [2]);

        await Assert.ThrowsAsync<ArchiveConflictException>(() => WalArchiver.ArchiveAsync(src, archive, "seg", TestContext.Current.CancellationToken));
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(archive, "seg")));
    }

    [Fact]
    public async Task Missing_or_empty_source_is_refused()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => WalArchiver.ArchiveAsync(Path.Combine(dir.Path, "nope"), dir.Path, "x", TestContext.Current.CancellationToken));
        var empty = Path.Combine(dir.Path, "empty");
        File.WriteAllBytes(empty, []);
        await Assert.ThrowsAsync<IOException>(() => WalArchiver.ArchiveAsync(empty, dir.Path, "x", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/seg")]
    [InlineData("..")]
    public async Task Archive_name_must_be_a_plain_file_name(string name)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var src = Path.Combine(dir.Path, "seg");
        File.WriteAllBytes(src, [1]);
        await Assert.ThrowsAsync<ArgumentException>(() => WalArchiver.ArchiveAsync(src, dir.Path, name, TestContext.Current.CancellationToken));
    }
}
