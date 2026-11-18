using Bazaar.Server.Vms.Images;

namespace Bazaar.Server.Vms.Tests.Images;

public sealed class ImageHasherTests
{
    [Fact]
    public async Task Hashes_an_empty_file()
    {
        using var dir = new TempImageDirectory();
        var path = dir.WriteFile("empty.img", string.Empty);

        var hash = await new ImageHasher(new PhysicalImageFileSystem()).HashFileAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(ImageTestData.EmptySha256, hash);
    }

    [Fact]
    public async Task Hashes_known_content_as_lowercase_hex()
    {
        using var dir = new TempImageDirectory();
        var path = dir.WriteFile("abc.img", "abc");

        var hash = await new ImageHasher(new PhysicalImageFileSystem()).HashFileAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(ImageTestData.AbcSha256, hash);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public async Task Hashes_a_large_file_the_same_as_the_in_memory_hash()
    {
        using var dir = new TempImageDirectory();
        var content = new string('x', 3 * 1024 * 1024 + 17);
        var path = dir.WriteFile("large.img", content);

        var hash = await new ImageHasher(new PhysicalImageFileSystem()).HashFileAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(ImageTestData.Sha256Of(content), hash);
    }

    [Fact]
    public async Task Reads_the_file_in_chunks_rather_than_all_at_once()
    {
        var bytes = new byte[100_000];
        Random.Shared.NextBytes(bytes);
        var fs = new ChunkingFileSystem(bytes, maxChunk: 7);

        var hash = await new ImageHasher(fs).HashFileAsync("chunked.img", TestContext.Current.CancellationToken);

        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), hash);
        Assert.NotNull(fs.LastStream);
        Assert.True(fs.LastStream.ReadCalls > 1000, $"Expected many small reads, got {fs.LastStream.ReadCalls}.");
    }

    [Fact]
    public async Task Missing_file_is_an_error_not_a_hash()
    {
        using var dir = new TempImageDirectory();
        var hasher = new ImageHasher(new PhysicalImageFileSystem());

        await Assert.ThrowsAsync<FileNotFoundException>(() => hasher.HashFileAsync(dir.FilePath("missing.img"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Requires_a_file_system()
    {
        Assert.Throws<ArgumentNullException>(() => new ImageHasher(null!));
    }

    [Fact]
    public async Task Rejects_an_empty_path()
    {
        var hasher = new ImageHasher(new PhysicalImageFileSystem());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => hasher.HashFileAsync(" ", TestContext.Current.CancellationToken));
    }

    /// <summary>Serves one byte array through a <see cref="ChunkedReadStream"/> and keeps the last stream it opened.</summary>
    private sealed class ChunkingFileSystem(byte[] bytes, int maxChunk) : IImageFileSystem
    {
        public ChunkedReadStream? LastStream { get; private set; }

        public bool FileExists(string path) => true;

        public Stream OpenRead(string path)
        {
            LastStream = new ChunkedReadStream(new MemoryStream(bytes, writable: false), maxChunk);
            return LastStream;
        }

        public long GetLength(string path) => bytes.Length;

        public bool IsReadOnly(string path) => true;

        public void SetReadOnly(string path, bool isReadOnly) => throw new NotSupportedException();
    }
}
