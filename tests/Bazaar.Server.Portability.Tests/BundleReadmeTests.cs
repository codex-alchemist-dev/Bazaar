using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Portability.Tests;

public sealed class BundleReadmeTests : IDisposable
{
    private static readonly DateTimeOffset _created = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-readme-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FlaggingSource(string id, string path, string[] flagged) : IPlaintextBundleSource
    {
        public string Id => id;

        public IReadOnlyList<string> PlaintextPersonalPaths => flagged;

        public async Task ExportAsync(IExportFileSink sink, CancellationToken ct)
        {
            await using var stream = await sink.OpenAsync(path, ct);
            await stream.WriteAsync("x"u8.ToArray(), ct);
        }
    }

    private BundleAssemblyOptions Options(string name) => new()
    {
        InstanceId = "inst-1",
        CreatedAt = _created,
        SigningKey = _key,
        OutputDirectory = Path.Combine(_root, name),
    };

    private static DelegateBundleSource Text(string id, string path) =>
        new(id, async (sink, ct) =>
        {
            await using var stream = await sink.OpenAsync(path, ct);
            await stream.WriteAsync("alpha"u8.ToArray(), ct);
        });

    private static Task<BundleAssemblyResult> Run(IEnumerable<IBundleSource> sources, BundleAssemblyOptions options) =>
        ExportBundleAssembler.AssembleAsync(sources, options, TestContext.Current.CancellationToken);

    private static string Readme(BundleAssemblyResult result) =>
        File.ReadAllText(Path.Combine(result.Directory, BundleReadme.FileName));

    [Fact]
    public async Task Bundle_has_a_readme_that_the_manifest_lists_and_the_signature_covers()
    {
        var result = await Run([Text("vault", "vault/a.md")], Options("a"));
        var bundle = await ExportBundleAssembler.ReadDirectoryAsync(result.Directory, TestContext.Current.CancellationToken);

        Assert.Contains(result.Files, f => f.Path == "README.txt");
        Assert.Empty(ExportBundleWriter.Verify(bundle));
        Assert.True(BundleSigner.Verify(bundle, _key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public async Task Readme_hash_in_the_manifest_matches_the_file()
    {
        var result = await Run([Text("vault", "vault/a.md")], Options("a"));
        var entry = result.Files.Single(f => f.Path == "README.txt");
        var bytes = File.ReadAllBytes(Path.Combine(result.Directory, "README.txt"));

        Assert.Equal(bytes.Length, entry.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), entry.Sha256);
    }

    [Fact]
    public async Task Readme_describes_only_the_folders_in_the_bundle()
    {
        var result = await Run([Text("vault", "vault/a.md"), Text("gc", "gc/1/events.jsonl")], Options("a"));
        var text = Readme(result);

        Assert.Contains("vault/", text);
        Assert.Contains("gc/", text);
        Assert.DoesNotContain("kanban/", text);
        Assert.DoesNotContain("audit/", text);
        Assert.Contains("inst-1", text);
        Assert.Contains("2026-11-22T09:00:00Z", text);
        Assert.Contains("authoritative", text);
    }

    [Fact]
    public async Task Readme_is_utf8_without_bom_and_uses_lf_only()
    {
        var result = await Run([Text("vault", "vault/a.md")], Options("a"));
        var bytes = File.ReadAllBytes(Path.Combine(result.Directory, "README.txt"));

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
    }

    [Fact]
    public async Task Readme_has_no_usage_instructions()
    {
        var result = await Run([Text("vault", "vault/a.md"), Text("gc", "gc/1/events.jsonl")], Options("a"));
        var text = Readme(result).ToLowerInvariant();

        foreach (var word in new[] { "how to", "command", "install", "terminal", "script" })
        {
            Assert.DoesNotContain(word, text);
        }
    }

    [Fact]
    public async Task Readme_is_deterministic()
    {
        var one = await Run([Text("vault", "vault/a.md")], Options("one"));
        var two = await Run([Text("vault", "vault/a.md")], Options("two"));

        Assert.Equal(Readme(one), Readme(two));
        Assert.Equal(one.Manifest, two.Manifest);
    }

    [Fact]
    public async Task A_source_cannot_write_the_readme_path()
    {
        var options = Options("a");

        await Assert.ThrowsAsync<ArgumentException>(() => Run([Text("x", "README.txt")], options));
        await Assert.ThrowsAsync<ArgumentException>(() => Run([Text("x", "readme.txt")], Options("b")));

        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    [Fact]
    public void Readme_for_an_empty_bundle_says_so()
    {
        var text = Encoding.UTF8.GetString(BundleReadme.Build("inst-1", _created, [], []));

        Assert.Contains("no data folders", text);
        Assert.DoesNotContain("Readable personal data", text);
    }

    [Fact]
    public void Readme_names_a_folder_it_has_no_words_for()
    {
        var text = Encoding.UTF8.GetString(BundleReadme.Build("inst-1", _created, ["extra/f.txt"], []));

        Assert.Contains("extra/  Not described in this note.", text);
    }

    [Fact]
    public async Task Plaintext_file_is_flagged_in_the_manifest_and_the_readme()
    {
        var source = new FlaggingSource("personal", "personal/alice.json", ["personal/alice.json"]);
        var result = await Run([source, Text("vault", "vault/a.md")], Options("a"));

        using var manifest = JsonDocument.Parse(result.Manifest);
        var entries = manifest.RootElement.GetProperty("files").EnumerateArray().ToDictionary(e => e.GetProperty("path").GetString()!);
        Assert.True(entries["personal/alice.json"].GetProperty("plaintextPersonal").GetBoolean());
        Assert.False(entries["vault/a.md"].TryGetProperty("plaintextPersonal", out _));
        Assert.False(entries["README.txt"].TryGetProperty("plaintextPersonal", out _));
        var text = Readme(result);
        Assert.Contains("Readable personal data", text);
        Assert.Contains("personal/alice.json", text);
    }

    [Fact]
    public async Task Bundle_without_plaintext_has_no_flags_anywhere()
    {
        var result = await Run([Text("vault", "vault/a.md")], Options("a"));

        Assert.DoesNotContain("plaintextPersonal", result.Manifest);
        Assert.DoesNotContain("Readable personal data", Readme(result));
    }

    [Fact]
    public async Task Flagged_bundle_still_verifies_and_is_signed()
    {
        var source = new FlaggingSource("personal", "personal/alice.json", ["personal/alice.json"]);
        var result = await Run([source], Options("a"));
        var bundle = await ExportBundleAssembler.ReadDirectoryAsync(result.Directory, TestContext.Current.CancellationToken);

        Assert.Empty(ExportBundleWriter.Verify(bundle));
        Assert.True(BundleSigner.Verify(bundle, _key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public async Task Flagging_a_file_the_source_did_not_write_aborts_the_bundle()
    {
        var source = new FlaggingSource("personal", "personal/alice.enc.jsonl", ["personal/ghost.json"]);
        var options = Options("a");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run([source], options));

        Assert.False(Directory.Exists(options.OutputDirectory));
    }
}
