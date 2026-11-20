using System.Text;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.SealedStorage.Tests;

public sealed class SealedStoreTests : IDisposable
{
    private const string _canary = "CANARY-soul-charter-do-not-leak-7731";
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
        var gen = await scope.WriteAsync("charter.md", Bytes(_canary), expectedGeneration: 0);
        var read = await scope.ReadAsync("charter.md");
        Assert.Equal(_canary, Encoding.UTF8.GetString(read!.Content));
        Assert.Equal(gen, read.Generation);
        Assert.Equal(["charter.md"], (await scope.ListAsync()).Select(e => e.Path));
        Assert.Null(await scope.ReadAsync("missing.md"));
    }

    [Fact]
    public async Task Nothing_on_disk_contains_plaintext_or_the_path()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteAsync("agents/ada/charter.md", Bytes(_canary));
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("kek.key", StringComparison.Ordinal)))
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain(_canary, text, StringComparison.Ordinal);
            Assert.DoesNotContain("charter", Path.GetFileName(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_flipped_bit_in_a_file_fails_closed_and_is_audited()
    {
        using (var scope = await Store().OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("charter.md", Bytes(_canary));
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
        File.WriteAllText(Path.Combine(ScopeDir(), "evil.bzs"), _canary);
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
        await scope.WriteAsync("a.md", Bytes(_canary));
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

    [Fact]
    public async Task Deleting_the_anchor_after_writes_is_tamper_not_a_silent_reanchor()
    {
        var anchorDir = Path.Combine(_dir, "anchors");
        using (var scope = await Store(anchor: new FileSealAnchor(anchorDir)).OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("a.md", Bytes("one"));
        }

        foreach (var f in Directory.GetFiles(anchorDir))
        {
            File.Delete(f);
        }

        await Assert.ThrowsAsync<SealTamperedException>(async () => await Store(anchor: new FileSealAnchor(anchorDir)).OpenScopeAsync("soul:1"));
    }

    [Fact]
    public async Task Deleting_the_manifest_while_files_remain_is_tamper()
    {
        using (var scope = await Store().OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("a.md", Bytes("one"));
        }

        File.Delete(Path.Combine(ScopeDir(), "manifest.bzm"));
        await Assert.ThrowsAsync<SealTamperedException>(async () => await Store(anchor: new InMemorySealAnchor()).OpenScopeAsync("soul:1"));
    }

    [Fact]
    public async Task A_file_moved_to_another_scope_fails()
    {
        var store = Store();
        using var one = await store.OpenScopeAsync("soul:1");
        using var two = await store.OpenScopeAsync("soul:2");
        await one.WriteAsync("a.md", Bytes("secret"));
        await two.WriteAsync("a.md", Bytes("other"));
        var dirs = Directory.GetDirectories(Path.Combine(_dir, "root"));
        var f1 = Directory.GetFiles(dirs[0], "*.bzs").Single();
        var f2 = Directory.GetFiles(dirs[1], "*.bzs").Single();
        File.Copy(f1, f2, overwrite: true);
        await Assert.ThrowsAsync<SealTamperedException>(async () => await VerifyBoth(one, two));
    }

    private static async Task VerifyBoth(ISealedScope a, ISealedScope b)
    {
        await a.VerifyAsync();
        await b.VerifyAsync();
    }

    [Fact]
    public async Task A_leftover_file_from_a_crash_before_the_manifest_commit_does_not_block_the_next_write()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteAsync("a.md", Bytes("one"));
        var existing = Directory.GetFiles(ScopeDir(), "*.bzs").Single();
        // Simulate the crash: a file for the next generation exists but the manifest never committed it.
        var next = existing[..existing.LastIndexOf('.', existing.LastIndexOf('.') - 1)] + ".2.bzs";
        File.WriteAllText(next, "leftover");
        var g = await scope.WriteAsync("a.md", Bytes("two"));
        Assert.Equal("two", Encoding.UTF8.GetString((await scope.ReadAsync("a.md"))!.Content));
        Assert.True(g >= 2);
    }

    [Fact]
    public async Task Lone_surrogates_in_paths_and_scopes_are_rejected()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await Assert.ThrowsAsync<ArgumentException>(async () => await scope.WriteAsync("notes\uD800", Bytes("x")));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Store().OpenScopeAsync("soul:x\uD800"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await scope.WriteAsync("a\nb", Bytes("x")));
    }

    [Fact]
    public async Task A_planted_oversize_file_is_tamper_without_reading_it()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteAsync("a.md", Bytes("one"));
        var file = Directory.GetFiles(ScopeDir(), "*.bzs").Single();
        await using (var stream = new FileStream(file, FileMode.Open))
        {
            stream.SetLength(300L * 1024 * 1024);
        }

        await Assert.ThrowsAsync<SealTamperedException>(async () => await scope.ReadAsync("a.md"));
    }

    [Fact]
    public async Task A_second_store_instance_serialises_with_the_first_through_the_scope_lock()
    {
        using var a = await Store().OpenScopeAsync("soul:1");
        using var b = await Store().OpenScopeAsync("soul:1");
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => (i % 2 == 0 ? a : b).WriteAsync($"f{i}.md", Bytes("x")).AsTask()));
        Assert.Equal(10, (await a.ListAsync()).Count);
        await b.VerifyAsync();
    }

    [Fact]
    public async Task Dispose_waits_for_in_flight_work_and_later_use_throws()
    {
        var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteAsync("a.md", Bytes("one"));
        scope.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await scope.ReadAsync("a.md"));
    }

    [Fact]
    public async Task A_concurrent_writer_never_yields_a_torn_snapshot()
    {
        using var writer = await Store().OpenScopeAsync("vault:acme");
        using var reader = await Store().OpenScopeAsync("vault:acme");
        await writer.WriteBatchAsync([SealedWrite.Put("refs/heads/main", Bytes("0")), SealedWrite.Put("objects/pack/p.pack", Bytes("0"))]);
        using var stop = new CancellationTokenSource();
        var writes = Task.Run(async () =>
        {
            for (var i = 1; !stop.IsCancellationRequested; i++)
            {
                await writer.WriteBatchAsync([SealedWrite.Put("objects/pack/p.pack", Bytes($"{i}")), SealedWrite.Put("refs/heads/main", Bytes($"{i}"))]);
                await Task.Delay(1);
            }
        });

        var generations = new HashSet<long>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        for (var n = 0; (n < 60 || generations.Count < 3) && DateTime.UtcNow < deadline; n++)
        {
            var snapshot = await reader.ReadAllAsync();
            generations.Add(snapshot.ManifestGeneration);
            Assert.Equal(["objects/pack/p.pack", "refs/heads/main"], snapshot.Files.Select(f => f.Path));
            Assert.Equal(Encoding.UTF8.GetString(snapshot.Files[0].Content), Encoding.UTF8.GetString(snapshot.Files[1].Content));
            Assert.All(snapshot.Files, f => Assert.Equal(snapshot.Files[0].Generation, f.Generation));
        }

        await stop.CancelAsync();
        await writes;
        Assert.True(generations.Count > 1, "the writer should have committed while the reader was reading");
    }

    [Fact]
    public async Task A_stale_delete_conflicts_and_deletes_nothing()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        var first = await scope.WriteAsync("a.md", Bytes("one"));
        var second = await scope.WriteAsync("a.md", Bytes("two"), first);
        var ex = await Assert.ThrowsAsync<SealGenerationConflictException>(async () => await scope.DeleteAsync("a.md", first));
        Assert.Equal(second, ex.Actual);
        Assert.Equal("two", Encoding.UTF8.GetString((await scope.ReadAsync("a.md"))!.Content));
        await Assert.ThrowsAsync<SealGenerationConflictException>(async () => await scope.DeleteAsync("a.md", 0));
        await Assert.ThrowsAsync<SealGenerationConflictException>(async () => await scope.DeleteAsync("missing.md", 5));
        Assert.False(await scope.DeleteAsync("missing.md", 0));
        Assert.True(await scope.DeleteAsync("a.md", second));
        Assert.Null(await scope.ReadAsync("a.md"));
        Assert.False(await scope.DeleteAsync("a.md"));
    }

    [Fact]
    public async Task A_batch_commits_once_with_deletes_and_checks_every_generation_first()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        var a = await scope.WriteAsync("a.md", Bytes("a1"));
        var b = await scope.WriteAsync("b.md", Bytes("b1"));
        var generation = await scope.WriteBatchAsync([SealedWrite.Put("a.md", Bytes("a2"), a), SealedWrite.Put("c.md", Bytes("c1"), 0), SealedWrite.Delete("b.md", b)]);
        var snapshot = await scope.ReadAllAsync();
        Assert.Equal(generation, snapshot.ManifestGeneration);
        Assert.Equal(b + 1, generation);
        Assert.Equal(["a.md", "c.md"], snapshot.Files.Select(f => f.Path));
        Assert.Equal("a2", Encoding.UTF8.GetString(snapshot.Files[0].Content));

        // One stale expectation fails the whole batch before anything is written.
        await Assert.ThrowsAsync<SealGenerationConflictException>(async () => await scope.WriteBatchAsync([SealedWrite.Put("d.md", Bytes("d1"), 0), SealedWrite.Put("a.md", Bytes("a3"), a)]));
        Assert.Equal(generation, (await scope.ReadAllAsync()).ManifestGeneration);
        Assert.Null(await scope.ReadAsync("d.md"));
        Assert.Empty((await scope.VerifyAsync()).UnreferencedFiles);
        await Assert.ThrowsAsync<ArgumentException>(async () => await scope.WriteBatchAsync([SealedWrite.Put("x.md", Bytes("1")), SealedWrite.Delete("x.md")]));
        Assert.Equal(generation, await scope.WriteBatchAsync([SealedWrite.Delete("never.md")]));
    }

    [Fact]
    public async Task A_batch_that_fails_part_way_leaves_the_old_state_fully_intact()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteAsync("a.md", Bytes("a1"));
        await scope.WriteAsync("b.md", Bytes("b1"));
        var before = await scope.ReadAllAsync();
        var filesBefore = Directory.GetFiles(ScopeDir()).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();

        // The third write's content throws when it is sealed: two new files are already on disk at that point.
        using var failing = new ThrowingMemory(16);
        await Assert.ThrowsAsync<IOException>(async () => await scope.WriteBatchAsync(
            [SealedWrite.Put("a.md", Bytes("a2")), SealedWrite.Put("c.md", Bytes("c1")), SealedWrite.Put("d.md", failing.Memory), SealedWrite.Delete("b.md")]));

        var after = await scope.ReadAllAsync();
        Assert.Equal(before.ManifestGeneration, after.ManifestGeneration);
        Assert.Equal(before.Files.Select(f => (f.Path, f.Generation, Encoding.UTF8.GetString(f.Content))), after.Files.Select(f => (f.Path, f.Generation, Encoding.UTF8.GetString(f.Content))));
        Assert.Equal(filesBefore, Directory.GetFiles(ScopeDir()).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty((await scope.VerifyAsync()).UnreferencedFiles);
        using var reopened = await Store().OpenScopeAsync("soul:1");
        Assert.Equal("b1", Encoding.UTF8.GetString((await reopened.ReadAsync("b.md"))!.Content));
    }

    [Fact]
    public async Task A_batch_over_the_bulk_limit_is_refused_before_any_write()
    {
        var store = new SealedStore(new SealedStoreOptions { RootPath = Path.Combine(_dir, "root"), MaxBulkBytes = 10 }, FileKekProvider.Open(Path.Combine(_dir, "kek.key")), _anchor, _audit);
        using var scope = await store.OpenScopeAsync("soul:1");
        await Assert.ThrowsAsync<ArgumentException>(async () => await scope.WriteBatchAsync([SealedWrite.Put("a.md", new byte[6]), SealedWrite.Put("b.md", new byte[6])]));
        Assert.Empty(await scope.ListAsync());
        await scope.WriteAsync("a.md", new byte[8]);
        await scope.WriteAsync("b.md", new byte[8]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await scope.ReadAllAsync());
    }

    [Fact]
    public async Task A_tampered_file_fails_the_whole_snapshot()
    {
        using var scope = await Store().OpenScopeAsync("soul:1");
        await scope.WriteBatchAsync([SealedWrite.Put("a.md", Bytes("a")), SealedWrite.Put("b.md", Bytes("b"))]);
        var file = Directory.GetFiles(ScopeDir(), "*.bzs").First();
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(file, bytes);
        await Assert.ThrowsAsync<SealTamperedException>(async () => await scope.ReadAllAsync());
        Assert.Contains(_audit.Events, e => e.Kind == "seal.tamper");
    }

    [Fact]
    public async Task The_lease_excludes_a_second_store_instance_until_released()
    {
        using var a = await Store().OpenScopeAsync("vault:acme");
        using var b = await Store().OpenScopeAsync("vault:acme");
        var lease = await a.AcquireLeaseAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("vault:acme", lease.Scope);
        await Assert.ThrowsAsync<TimeoutException>(async () => await b.AcquireLeaseAsync(TimeSpan.FromMilliseconds(200)));

        // Single operations do not need the lease; they stay serialised by the per-operation lock.
        await b.WriteAsync("a.md", Bytes("x"));
        var waiting = b.AcquireLeaseAsync(TimeSpan.FromSeconds(10)).AsTask();
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        lease.Dispose();
        using var second = await waiting;
        Assert.Empty((await a.VerifyAsync()).UnreferencedFiles);
    }

    [Fact]
    public async Task Leftover_temp_files_of_a_crash_are_removed_and_the_scope_opens_clean()
    {
        using (var scope = await Store().OpenScopeAsync("soul:1"))
        {
            await scope.WriteAsync("a.md", Bytes("one"));
        }

        var dir = ScopeDir();
        var blob = Directory.GetFiles(dir, "*.bzs").Single();
        File.WriteAllText(Path.Combine(dir, $"manifest.bzm.{Guid.NewGuid():N}.tmp"), "half a manifest");
        File.WriteAllText($"{blob[..blob.LastIndexOf('.', blob.LastIndexOf('.') - 1)]}.9.bzs.{Guid.NewGuid():N}.tmp", "half a blob");

        using var reopened = await Store().OpenScopeAsync("soul:1");
        Assert.Equal("one", Encoding.UTF8.GetString((await reopened.ReadAsync("a.md"))!.Content));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Contains(_audit.Events, e => e.Kind == "seal.cleanup");
        Assert.Empty((await reopened.VerifyAsync()).UnreferencedFiles);
        await reopened.WriteAsync("a.md", Bytes("two"));
        Assert.Equal("two", Encoding.UTF8.GetString((await reopened.ReadAsync("a.md"))!.Content));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>Memory whose span throws when read, to inject a failure in the middle of a batch.</summary>
    private sealed class ThrowingMemory(int length) : System.Buffers.MemoryManager<byte>
    {
        public new Memory<byte> Memory => CreateMemory(length);

        public override Span<byte> GetSpan() => throw new IOException("Injected failure.");

        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => throw new IOException("Injected failure.");

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
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
