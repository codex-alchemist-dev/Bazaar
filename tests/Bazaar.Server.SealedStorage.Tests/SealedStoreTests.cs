using System.Text;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.SealedStorage.Tests;

public sealed class SealedStoreTests : IDisposable
{
    private const string Canary = "CANARY-soul-charter-do-not-leak-7731";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-sealed-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSink _audit = new();
    private readonly InMemorySealAnchor _anchor = new();

    public SealedStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SealedStore Store(string? kekFile = null, ISealAnchor? anchor = null) =>
        new(new SealedStoreOptions { RootPath = Path.Combine(_dir, "root") }, FileKekProvider.Open(Path.Combine(_dir, kekFile ?? "kek.key")), anchor ?? _anchor, _audit);

    private string ScopeDir() => Directory.GetDirectories(Path.Combine(_dir, "root")).Single();

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Round_trips_and_lists_from_the_manifest()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        var gen = await scope.WriteAsync("charter.md", Bytes(Canary), expectedGeneration: 0);
        var read = await scope.ReadAsync("charter.md");
        Assert.Equal(Canary, Encoding.UTF8.GetString(read!.Content));
        Assert.Equal(gen, read.Generation);
        Assert.Equal(["charter.md"], (await scope.ListAsync()).Select(e => e.Path));
        Assert.Null(await scope.ReadAsync("missing.md"));
    }

    [Fact]
    public async Task Nothing_on_disk_contains_plaintext_or_the_path()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteAsync("agents/ada/charter.md", Bytes(Canary));
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("kek.key", StringComparison.Ordinal)))
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
            Assert.DoesNotContain("charter", Path.GetFileName(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_flipped_bit_in_a_file_fails_closed_and_is_audited()
    {
        using (var scope = await Store().OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("charter.md", Bytes(Canary));
        }

        var file = Directory.GetFiles(ScopeDir(), "*.bzs").Single();
        var bytes = File.ReadAllBytes(file);
        bytes[^20] ^= 0x01;
        File.WriteAllBytes(file, bytes);

        using var reopened = await Store().OpenScopeAsync("soul:1");
        var ex = await Assert.ThrowsAsync<SealTamperedException>(async () => await reopened.ReadAsync("charter.md"));
        Assert.Equal("charter.md", ex.Path);
        Assert.Contains(_audit.Events, e => e.Kind == "seal.tamper" && e.Path == "charter.md");
    }

    [Fact]
    public async Task Truncated_and_deleted_files_fail_closed()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteAsync("a.md", Bytes("alpha"));
        await scope.WriteAsync("b.md", Bytes("beta"));
        var files = Directory.GetFiles(ScopeDir(), "*.bzs").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        File.WriteAllBytes(files[0], File.ReadAllBytes(files[0])[..10]);
        File.Delete(files[1]);
        await Assert.ThrowsAsync<SealTamperedException>(async () => await scope.VerifyAsync());
        foreach (var p in new[] { "a.md", "b.md" })
        {
            await Assert.ThrowsAsync<SealTamperedException>(async () => await scope.ReadAsync(p));
        }

        Assert.Contains(_audit.Events, e => e.Kind == "seal.tamper");
    }

    [Fact]
    public async Task A_file_swapped_with_another_sealed_file_fails()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteAsync("a.md", Bytes("alpha"));
        await scope.WriteAsync("b.md", Bytes("beta"));
        var files = Directory.GetFiles(ScopeDir(), "*.bzs");
        var first = File.ReadAllBytes(files[0]);
        File.WriteAllBytes(files[0], File.ReadAllBytes(files[1]));
        File.WriteAllBytes(files[1], first);
        await Assert.ThrowsAsync<SealTamperedException>(async () => await scope.ReadAsync("a.md"));
        await Assert.ThrowsAsync<SealTamperedException>(async () => await scope.ReadAsync("b.md"));
    }

    [Fact]
    public async Task Restoring_an_older_copy_of_a_file_fails()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteAsync("a.md", Bytes("version one"));
        var oldFile = Directory.GetFiles(ScopeDir(), "*.bzs").Single();
        var oldBytes = File.ReadAllBytes(oldFile);
        var oldName = oldFile;
        await scope.WriteAsync("a.md", Bytes("version two"));
        File.WriteAllBytes(oldName, oldBytes);
        Assert.Equal("version two", Encoding.UTF8.GetString((await scope.ReadAsync("a.md"))!.Content));
        var current = Directory.GetFiles(ScopeDir(), "*.bzs").Single(f => f != oldName);
        File.WriteAllBytes(current, oldBytes);
        await Assert.ThrowsAsync<SealTamperedException>(async () => await scope.ReadAsync("a.md"));
    }

    [Fact]
    public async Task Rolling_back_the_whole_scope_is_caught_by_the_anchor()
    {
        using (var scope = await Store().OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("a.md", Bytes("one"));
        }

        var snapshot = Path.Combine(_dir, "snapshot");
        CopyDirectory(ScopeDir(), snapshot);
        using (var scope = await Store().OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("a.md", Bytes("two"));
        }

        var scopeDir = ScopeDir();
        Directory.Delete(scopeDir, recursive: true);
        CopyDirectory(snapshot, scopeDir);
        await Assert.ThrowsAsync<SealTamperedException>(async () => await Store().OpenScopeAsync("soul:1"));
    }

    [Fact]
    public async Task Edited_manifest_or_key_header_fails_closed()
    {
        using (var scope = await Store().OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("a.md", Bytes("one"));
        }

        var manifest = Path.Combine(ScopeDir(), "manifest.bzm");
        var bytes = File.ReadAllBytes(manifest);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(manifest, bytes);
        await Assert.ThrowsAsync<SealTamperedException>(async () => await Store().OpenScopeAsync("soul:1"));
    }

    [Fact]
    public async Task Missing_key_header_in_a_populated_scope_fails_and_does_not_create_a_new_key()
    {
        using (var scope = await Store().OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("a.md", Bytes("one"));
        }

        File.Delete(Path.Combine(ScopeDir(), "scope.key"));
        await Assert.ThrowsAsync<SealTamperedException>(async () => await Store().OpenScopeAsync("soul:1"));
        Assert.False(File.Exists(Path.Combine(ScopeDir(), "scope.key")));
    }

    [Fact]
    public async Task A_different_KEK_cannot_open_the_scope()
    {
        using (var scope = await Store().OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("a.md", Bytes("one"));
        }

        await Assert.ThrowsAsync<SealTamperedException>(async () => await Store("other-kek.key").OpenScopeAsync("soul:1"));
    }

    [Fact]
    public async Task Scopes_cannot_read_each_other()
    {
        var store = Store();
        using var one = await store.OpenScopeAsync("soul:1");
        using var two = await store.OpenScopeAsync("soul:2");
        await one.WriteAsync("a.md", Bytes("one"));
        Assert.Null(await two.ReadAsync("a.md"));
    }

    [Fact]
    public async Task Unknown_files_are_never_read_and_are_reported_by_verify()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteAsync("a.md", Bytes("one"));
        File.WriteAllText(Path.Combine(ScopeDir(), "evil.bzs"), Canary);
        Assert.Single(await scope.ListAsync());
        var report = await scope.VerifyAsync();
        Assert.Equal(["evil.bzs"], report.UnreferencedFiles);
        Assert.Contains(_audit.Events, e => e.Kind == "seal.unreferenced");
    }

    [Fact]
    public async Task Stale_expected_generation_is_rejected_and_create_only_works()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        var g1 = await scope.WriteAsync("a.md", Bytes("one"), expectedGeneration: 0);
        await Assert.ThrowsAsync<SealGenerationConflictException>(async () => await scope.WriteAsync("a.md", Bytes("x"), expectedGeneration: 0));
        var g2 = await scope.WriteAsync("a.md", Bytes("two"), expectedGeneration: g1);
        Assert.True(g2 > g1);
        await Assert.ThrowsAsync<SealGenerationConflictException>(async () => await scope.WriteAsync("a.md", Bytes("x"), expectedGeneration: g1));
    }

    [Fact]
    public async Task Delete_removes_the_entry_and_recreate_never_reuses_a_generation()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        var g1 = await scope.WriteAsync("a.md", Bytes("one"));
        Assert.True(await scope.DeleteAsync("a.md"));
        Assert.False(await scope.DeleteAsync("a.md"));
        Assert.Null(await scope.ReadAsync("a.md"));
        var g2 = await scope.WriteAsync("a.md", Bytes("two"));
        Assert.True(g2 > g1);
    }

    [Fact]
    public async Task Shred_makes_data_unrecoverable_and_forgets_the_anchor()
    {
        var store = Store();
        var scope = await store.OpenScopeAsync("soul:1");
        await scope.WriteAsync("a.md", Bytes(Canary));
        await scope.ShredAsync();
        Assert.False(Directory.Exists(Path.Combine(_dir, "root")) && Directory.GetDirectories(Path.Combine(_dir, "root")).Length > 0);
        Assert.Null(await _anchor.GetAsync("soul:1", CancellationToken.None));
        using var fresh = await store.OpenScopeAsync("soul:1");
        Assert.Empty(await fresh.ListAsync());
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/abs")]
    [InlineData("C:/x")]
    [InlineData("a//b")]
    [InlineData("")]
    public async Task Bad_logical_paths_are_rejected(string path)
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await Assert.ThrowsAsync<ArgumentException>(async () => await scope.WriteAsync(path, Bytes("x")));
    }

    [Fact]
    public async Task Concurrent_writers_with_one_expected_generation_have_exactly_one_winner()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        var g = await scope.WriteAsync("a.md", Bytes("base"));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try
            {
                await scope.WriteAsync("a.md", Bytes($"w{i}"), expectedGeneration: g);
                return true;
            }
            catch (SealGenerationConflictException)
            {
                return false;
            }
        }));
        Assert.Single(results, r => r);
        await scope.VerifyAsync();
    }

    [Fact]
    public async Task File_anchor_survives_a_restart_and_catches_rollback()
    {
        var anchorDir = Path.Combine(_dir, "anchors");
        using (var scope = await Store(anchor: new FileSealAnchor(anchorDir)).OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("a.md", Bytes("one"));
        }

        var snapshot = Path.Combine(_dir, "snapshot");
        CopyDirectory(ScopeDir(), snapshot);
        using (var scope = await Store(anchor: new FileSealAnchor(anchorDir)).OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("a.md", Bytes("two"));
        }

        var dir = ScopeDir();
        Directory.Delete(dir, recursive: true);
        CopyDirectory(snapshot, dir);
        await Assert.ThrowsAsync<SealTamperedException>(async () => await Store(anchor: new FileSealAnchor(anchorDir)).OpenScopeAsync("soul:1"));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

    private sealed class RecordingSink : ISealAuditSink
    {
        public List<SealAuditEvent> Events { get; } = [];

        public ValueTask RecordAsync(SealAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add(auditEvent);
            }

            return ValueTask.CompletedTask;
        }
    }
}
