using Bazaar.Server.Vms.Images;

namespace Bazaar.Server.Vms.Tests.Images;

public sealed class GoldenImageSealerTests
{
    [Fact]
    public async Task Seal_records_the_hash_and_the_recipe_and_sets_read_only()
    {
        using var dir = new TempImageDirectory();
        const string content = "built parent disk";
        var path = dir.WriteFile("linux-v3.img", content);
        var fs = new PhysicalImageFileSystem();
        var sealer = new GoldenImageSealer(fs, new ImageHasher(fs));

        var sealedParent = await sealer.SealAsync(ImageTestData.LinuxRecipe(version: 3), path, TestContext.Current.CancellationToken);

        Assert.Equal(GoldenImageOs.Linux, sealedParent.Os);
        Assert.Equal(3, sealedParent.Version);
        Assert.Equal(ImageTestData.Sha256Of(content), sealedParent.Sha256);
        Assert.Equal("1.4.0", sealedParent.GuestAgentVersion);
        Assert.Equal(path, sealedParent.Path);
        Assert.True(fs.IsReadOnly(path));
    }

    [Fact]
    public async Task Seal_sets_read_only_after_hashing_and_only_once()
    {
        const string path = "parent.img";
        var fs = new FakeImageFileSystem();
        fs.Add(path, "parent"u8.ToArray());
        var sealer = new GoldenImageSealer(fs, new ImageHasher(fs));

        await sealer.SealAsync(ImageTestData.LinuxRecipe(), path, TestContext.Current.CancellationToken);

        Assert.Equal(1, fs.MutationCount);
        Assert.True(fs.IsReadOnly(path));
    }

    [Fact]
    public async Task Refuses_to_seal_a_missing_file()
    {
        using var dir = new TempImageDirectory();
        var path = dir.FilePath("never-built.img");
        var fs = new PhysicalImageFileSystem();
        var sealer = new GoldenImageSealer(fs, new ImageHasher(fs));

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => sealer.SealAsync(ImageTestData.LinuxRecipe(), path, TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Sealing_twice_keeps_the_same_hash()
    {
        using var dir = new TempImageDirectory();
        var path = dir.WriteFile("parent.img", "same bytes");
        var fs = new PhysicalImageFileSystem();
        var sealer = new GoldenImageSealer(fs, new ImageHasher(fs));

        var first = await sealer.SealAsync(ImageTestData.LinuxRecipe(), path, TestContext.Current.CancellationToken);
        var second = await sealer.SealAsync(ImageTestData.LinuxRecipe(), path, TestContext.Current.CancellationToken);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.True(fs.IsReadOnly(path));
    }

    [Fact]
    public async Task Null_recipe_is_refused()
    {
        using var dir = new TempImageDirectory();
        var path = dir.WriteFile("parent.img", "bytes");
        var fs = new PhysicalImageFileSystem();
        var sealer = new GoldenImageSealer(fs, new ImageHasher(fs));

        await Assert.ThrowsAsync<ArgumentNullException>(() => sealer.SealAsync(null!, path, TestContext.Current.CancellationToken));
    }
}
