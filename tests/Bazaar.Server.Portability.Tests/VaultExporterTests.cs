using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Portability.Tests;

public class VaultExporterTests : IDisposable
{
    private const string _head = "0123456789abcdef0123456789abcdef01234567";
    private const string _repo = "/vaults/company";

    private static readonly byte[] _bundleBytes = Encoding.ASCII.GetBytes("# fixed git bundle bytes\n");
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] _expectedLayout = ["repo.bundle", "tree/Index.md", "tree/decisions/0001-use-vault.md", "tree/projects/x/Plan.md", "vault-manifest.json"];
    private static readonly string[] _expectedEmptyLayout = ["repo.bundle", "vault-manifest.json"];

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bazaar-vault-export-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, string> SampleVault() => new(StringComparer.Ordinal)
    {
        ["Index.md"] = "# Company\n\nSee [[projects/x/Plan]] and [[Auth service]].\n",
        ["projects/x/Plan.md"] = "---\nid: 01JABC\ntitle: Plan\n---\n# Plan\n\n- [[Auth service#Token flow]]\n",
        ["decisions/0001-use-vault.md"] = "# Use the Vault\n",
    };

    private static byte[] Sha256(byte[] content) => SHA256.HashData(content);

    private static string Hex(byte[] content) => Convert.ToHexString(Sha256(content)).ToLowerInvariant();

    private static async Task<(VaultExportManifest Manifest, MemoryVaultExportSink Sink, FakeBundleWriter Bundles)> RunAsync(
        IReadOnlyDictionary<string, string> files)
    {
        var git = new FakeVaultGit(_head, files);
        var bundles = new FakeBundleWriter(_bundleBytes);
        var sink = new MemoryVaultExportSink();
        var manifest = await new VaultExporter(git, bundles).ExportAsync(_repo, "HEAD", sink, TestContext.Current.CancellationToken);
        return (manifest, sink, bundles);
    }

    [Fact]
    public async Task Tree_bundle_and_manifest_are_written_under_the_export_layout()
    {
        var (manifest, sink, _) = await RunAsync(SampleVault());

        Assert.Equal(_expectedLayout, sink.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        Assert.Equal(SampleVault()["Index.md"], sink.Text("tree/Index.md"));
        Assert.Equal(SampleVault()["projects/x/Plan.md"], sink.Text("tree/projects/x/Plan.md"));
        Assert.Equal(_bundleBytes, sink.Files["repo.bundle"]);
        Assert.Equal(_head, manifest.HeadCommit);
        Assert.Equal(4, manifest.FileCount);
    }

    [Fact]
    public async Task Wiki_links_and_line_endings_survive_byte_for_byte()
    {
        const string text = "# Links\r\n\r\n[[projects/x/Plan|the plan]] [[Auth service#^a1b2c3]] [[#Token flow]]\r\n- [[Auth service]]\r\n```\n[[not a link in code]]\n```\nno final newline [[Last]]";
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["links.md"] = text };

        var (_, sink, _) = await RunAsync(files);

        Assert.Equal(_utf8.GetBytes(text), sink.Files["tree/links.md"]);
    }

    [Theory]
    [InlineData("/etc/passwd.md")]
    [InlineData("../outside.md")]
    [InlineData("notes/../../outside.md")]
    [InlineData("notes/./x.md")]
    [InlineData("a\\b.md")]
    [InlineData("C:x.md")]
    [InlineData("notes//double.md")]
    public async Task Path_escapes_are_refused_before_anything_is_written(string badPath)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["good.md"] = "ok\n", [badPath] = "bad\n" };
        var git = new FakeVaultGit(_head, files);
        var bundles = new FakeBundleWriter(_bundleBytes);
        var sink = new MemoryVaultExportSink();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new VaultExporter(git, bundles).ExportAsync(_repo, "HEAD", sink, TestContext.Current.CancellationToken));

        Assert.Empty(sink.OpenedPaths);
        Assert.Empty(bundles.RepoPaths);
    }

    [Fact]
    public async Task Paths_that_differ_only_by_case_are_refused()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["Plan.md"] = "a\n", ["plan.md"] = "b\n" };
        var sink = new MemoryVaultExportSink();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new VaultExporter(new FakeVaultGit(_head, files), new FakeBundleWriter(_bundleBytes))
                .ExportAsync(_repo, "HEAD", sink, TestContext.Current.CancellationToken));

        Assert.Empty(sink.OpenedPaths);
    }

    [Fact]
    public async Task Manifest_carries_the_plaintext_warning_and_every_hash()
    {
        var (manifest, sink, _) = await RunAsync(SampleVault());

        using var doc = JsonDocument.Parse(sink.Text("vault-manifest.json"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("plaintext").GetBoolean());
        var notice = root.GetProperty("plaintextNotice").GetString();
        Assert.NotNull(notice);
        Assert.Contains("plaintext copy", notice, StringComparison.Ordinal);
        Assert.Contains("doc 33 Q6", notice, StringComparison.Ordinal);
        Assert.Equal(VaultExportFormat.PlaintextNotice, notice);
        Assert.Equal("1.0.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(_head, root.GetProperty("headCommit").GetString());

        var listed = root.GetProperty("files").EnumerateArray()
            .Select(f => (Path: f.GetProperty("path").GetString(), Sha256: f.GetProperty("sha256").GetString(), Bytes: f.GetProperty("bytes").GetInt64()))
            .ToList();
        Assert.Equal(manifest.Files.Select(f => f.Path), listed.Select(f => f.Path));
        foreach (var file in listed)
        {
            var content = sink.Files[file.Path!];
            Assert.Equal(Hex(content), file.Sha256);
            Assert.Equal(content.Length, file.Bytes);
        }
        Assert.DoesNotContain("vault-manifest.json", listed.Select(f => f.Path));
    }

    [Fact]
    public async Task Bundle_entry_hashes_the_streamed_bundle_and_names_the_repository()
    {
        var (manifest, _, bundles) = await RunAsync(SampleVault());

        Assert.Equal(new[] { _repo }, bundles.RepoPaths);
        var bundle = manifest.Files.Single(f => f.Path == "repo.bundle");
        Assert.Equal(Hex(_bundleBytes), bundle.Sha256);
        Assert.Equal(_bundleBytes.Length, bundle.Bytes);
        Assert.Equal("repo.bundle", manifest.Files[0].Path);
    }

    [Fact]
    public async Task An_empty_bundle_is_refused()
    {
        var sink = new MemoryVaultExportSink();
        var exporter = new VaultExporter(new FakeVaultGit(_head, SampleVault()), new FakeBundleWriter([]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(_repo, "HEAD", sink, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Export_is_deterministic_whatever_order_git_lists_the_files()
    {
        var forward = SampleVault();
        var reversed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in forward.Reverse()) reversed[pair.Key] = pair.Value;

        var first = await RunAsync(forward);
        var second = await RunAsync(reversed);

        Assert.Equal(first.Manifest.ToJson(), second.Manifest.ToJson());
        Assert.Equal(first.Sink.Files.Keys.OrderBy(k => k, StringComparer.Ordinal), second.Sink.Files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (path, content) in first.Sink.Files)
        {
            Assert.Equal(content, second.Sink.Files[path]);
        }
    }

    [Fact]
    public async Task Export_twice_into_one_sink_path_is_refused_rather_than_replaced()
    {
        var sink = new MemoryVaultExportSink();
        var exporter = new VaultExporter(new FakeVaultGit(_head, SampleVault()), new FakeBundleWriter(_bundleBytes));

        await exporter.ExportAsync(_repo, "HEAD", sink, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(_repo, "HEAD", sink, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Directory_sink_writes_the_plain_tree_on_disk_with_the_same_bytes()
    {
        var sink = new DirectoryVaultExportSink(_tempRoot);
        var exporter = new VaultExporter(new FakeVaultGit(_head, SampleVault()), new FakeBundleWriter(_bundleBytes));

        var manifest = await exporter.ExportAsync(_repo, "HEAD", sink, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(_bundleBytes, await File.ReadAllBytesAsync(Path.Combine(_tempRoot, "repo.bundle"), ct));
        Assert.Equal(
            _utf8.GetBytes(SampleVault()["projects/x/Plan.md"]),
            await File.ReadAllBytesAsync(Path.Combine(_tempRoot, "tree", "projects", "x", "Plan.md"), ct));
        Assert.Equal(
            manifest.ToJson(),
            await File.ReadAllTextAsync(Path.Combine(_tempRoot, "vault-manifest.json"), _utf8, ct));
    }

    [Fact]
    public async Task Directory_sink_refuses_a_path_that_leaves_its_root()
    {
        var sink = new DirectoryVaultExportSink(_tempRoot);

        await Assert.ThrowsAsync<ArgumentException>(() => sink.OpenWriteAsync("tree/../../escape.md", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => sink.OpenWriteAsync("/tmp/escape.md", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Empty_vault_exports_a_bundle_and_an_empty_manifest()
    {
        var (manifest, sink, _) = await RunAsync(new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(_expectedEmptyLayout, sink.Files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(1, manifest.FileCount);
        Assert.Equal(_bundleBytes.Length, manifest.TotalBytes);
    }
}
