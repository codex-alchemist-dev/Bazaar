using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Portability.Tests;

public sealed class ExportBundleAssemblerTests : IDisposable
{
    private static readonly DateTimeOffset _created = new(2026, 11, 19, 8, 30, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-assembly-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private BundleAssemblyOptions Options(string name) => new()
    {
        InstanceId = "inst-1",
        CreatedAt = _created,
        SigningKey = _key,
        OutputDirectory = Path.Combine(_root, name),
    };

    private static Task<BundleAssemblyResult> Run(IEnumerable<IBundleSource> sources, BundleAssemblyOptions options) =>
        ExportBundleAssembler.AssembleAsync(sources, options, TestContext.Current.CancellationToken);

    private static Task<ExportBundle> Read(string directory) =>
        ExportBundleAssembler.ReadDirectoryAsync(directory, TestContext.Current.CancellationToken);

    private static DelegateBundleSource Text(string id, string path, string content) =>
        new DelegateBundleSource(id, async (sink, ct) =>
        {
            await using var stream = await sink.OpenAsync(path, ct);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
        });

    private static IBundleSource[] Sources() =>
    [
        Text("vault", "vault/a.md", "alpha"),
        Text("gc", "gc/1/events.jsonl", "{\"n\":1}\n{\"n\":2}\n"),
    ];

    [Fact]
    public async Task Assembles_verified_and_signed_bundle()
    {
        var result = await Run(Sources(), Options("a"));
        var bundle = await Read(result.Directory);

        Assert.Empty(ExportBundleWriter.Verify(bundle));
        Assert.True(BundleSigner.Verify(bundle, _key.ExportSubjectPublicKeyInfo()));
        Assert.Equal(
            ["gc/1/events.jsonl", "manifest.json", "manifest.pub", "manifest.sig", "vault/a.md"],
            bundle.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public async Task Manifest_records_size_and_sha256()
    {
        var result = await Run(Sources(), Options("a"));
        var entry = result.Files.Single(f => f.Path == "vault/a.md");

        Assert.Equal(5, entry.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData("alpha"u8)).ToLowerInvariant(), entry.Sha256);
        Assert.Contains(entry.Sha256, result.Manifest);
        Assert.Contains("\"fileCount\":2", result.Manifest);
    }

    [Fact]
    public async Task Same_input_gives_same_bytes_whatever_the_registration_order()
    {
        var one = await Run(Sources(), Options("one"));
        var two = await Run(Sources().Reverse(), Options("two"));

        Assert.Equal(one.Manifest, two.Manifest);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(one.Directory, "vault/a.md")),
            File.ReadAllBytes(Path.Combine(two.Directory, "vault/a.md")));
    }

    [Fact]
    public async Task Tampered_file_or_manifest_is_detected()
    {
        var result = await Run(Sources(), Options("a"));
        File.WriteAllText(Path.Combine(result.Directory, "vault", "a.md"), "alphX");
        var tamperedFile = await Read(result.Directory);
        Assert.Contains(ExportBundleWriter.Verify(tamperedFile), p => p.StartsWith("sha256 mismatch", StringComparison.Ordinal));

        File.WriteAllText(Path.Combine(result.Directory, "manifest.json"), result.Manifest.Replace("inst-1", "inst-2"));
        var tamperedManifest = await Read(result.Directory);
        Assert.False(BundleSigner.Verify(tamperedManifest, _key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public async Task Streaming_file_larger_than_the_buffer_is_hashed_whole()
    {
        var chunk = new byte[1000];
        new Random(7).NextBytes(chunk);
        var source = new DelegateBundleSource("big", async (sink, ct) =>
        {
            await using var stream = await sink.OpenAsync("graph/big.bin", ct);
            for (var i = 0; i < 50; i++) await stream.WriteAsync(chunk, ct);
        });
        var options = Options("a") with { BufferSize = 64 };

        var result = await Run([source], options);

        var expected = new byte[chunk.Length * 50];
        for (var i = 0; i < 50; i++) chunk.CopyTo(expected, i * chunk.Length);
        var entry = Assert.Single(result.Files);
        Assert.Equal(50_000, entry.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), entry.Sha256);
        Assert.Empty(ExportBundleWriter.Verify(await Read(result.Directory)));
    }

    [Fact]
    public async Task Failing_exporter_leaves_no_partial_bundle()
    {
        var failing = new DelegateBundleSource("zz-fail", async (sink, ct) =>
        {
            await using var stream = await sink.OpenAsync("kanban/tasks.jsonl", ct);
            await stream.WriteAsync("partial"u8.ToArray(), ct);
            throw new InvalidOperationException("boom");
        });
        var options = Options("out");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run([.. Sources(), failing], options));

        Assert.False(Directory.Exists(options.OutputDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("manifest.json")]
    [InlineData("manifest.sig")]
    public async Task Bad_paths_abort_the_bundle(string path)
    {
        var options = Options("out");
        await Assert.ThrowsAsync<ArgumentException>(
            () => Run([Text("x", path, "1")], options));
        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    [Fact]
    public async Task Duplicate_path_across_sources_aborts()
    {
        var options = Options("out");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run([Text("a", "x/f", "1"), Text("b", "X/F", "2")], options));
        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    [Fact]
    public async Task Cancellation_aborts_cleanly()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var options = Options("out");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ExportBundleAssembler.AssembleAsync(Sources(), options, cts.Token));
        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    [Fact]
    public async Task Optional_encryption_writes_an_archive_that_decrypts_to_the_same_bundle()
    {
        var archive = Path.Combine(_root, "bundle.bzex");
        var options = Options("a") with
        {
            EncryptedArchivePath = archive,
            Passphrase = "pass phrase",
            KdfParameters = new Argon2Parameters(ExportBundleCipher.MinMemoryKiB, ExportBundleCipher.MinTimeCost, 1),
        };

        var result = await Run(Sources(), options);

        Assert.Equal(archive, result.EncryptedArchivePath);
        var decrypted = ExportBundleCipher.Decrypt(await File.ReadAllBytesAsync(archive, TestContext.Current.CancellationToken), "pass phrase");
        Assert.Equal(result.Manifest, decrypted.Manifest);
        Assert.True(BundleSigner.Verify(decrypted, _key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public async Task Failed_encryption_leaves_neither_directory_nor_archive()
    {
        var archive = Path.Combine(_root, "bundle.bzex");
        var options = Options("a") with
        {
            EncryptedArchivePath = archive,
            Passphrase = "p",
            KdfParameters = new Argon2Parameters(1, 1, 1),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => Run(Sources(), options));

        Assert.False(Directory.Exists(options.OutputDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Refuses_a_non_empty_output_directory()
    {
        var options = Options("out");
        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "keep.txt"), "x");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Sources(), options));
        Assert.True(File.Exists(Path.Combine(options.OutputDirectory, "keep.txt")));
    }

    [Fact]
    public void Directory_name_follows_doc_44_layout()
    {
        Assert.Equal("bazaar-export-inst-1-20261119T083000Z", ExportBundleAssembler.DirectoryName("inst-1", _created));
    }
}
