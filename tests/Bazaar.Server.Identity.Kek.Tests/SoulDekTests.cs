using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.Identity.Kek.Tests;

/// <summary>Data-key custody (doc 14): create, reuse, open, crypto-shred, and re-wrap across several stores. Database-free.</summary>
public sealed class SoulDekTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-dek-" + Guid.CreateVersion7().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class MemoryDekStore : ISoulDekStore, IKekRewrapStore
    {
        public readonly Dictionary<Guid, (string Scope, byte[]? Wrapped, string KekId, bool Retired)> Rows = [];

        public Task<SoulDekRecord?> GetLiveAsync(string scope, CancellationToken ct)
        {
            var live = Rows.Where(r => r.Value.Scope == scope && !r.Value.Retired).Select(r => (SoulDekRecord?)new SoulDekRecord(r.Key, r.Value.Wrapped!, r.Value.KekId)).FirstOrDefault();
            return Task.FromResult(live);
        }

        public Task<bool> TryInsertLiveAsync(Guid dekId, string scope, byte[] wrapped, string kekId, CancellationToken ct)
        {
            if (Rows.Values.Any(r => r.Scope == scope && !r.Retired))
                return Task.FromResult(false);
            Rows[dekId] = (scope, wrapped, kekId, false);
            return Task.FromResult(true);
        }

        public Task<Guid?> ShredAsync(string scope, CancellationToken ct)
        {
            var hit = Rows.FirstOrDefault(r => r.Value.Scope == scope && !r.Value.Retired);
            if (hit.Key == Guid.Empty)
                return Task.FromResult<Guid?>(null);
            Rows[hit.Key] = (scope, null, hit.Value.KekId, true);
            return Task.FromResult<Guid?>(hit.Key);
        }

        public Task<IReadOnlyList<KekWrapRecord>> ListLiveWrapsAsync(Guid? afterId, int limit, CancellationToken ct)
        {
            IReadOnlyList<KekWrapRecord> page = Rows
                .Where(r => !r.Value.Retired && (afterId is null || r.Key.CompareTo(afterId.Value) > 0))
                .OrderBy(r => r.Key)
                .Take(limit)
                .Select(r => new KekWrapRecord(r.Key, r.Value.Wrapped!, r.Key.ToByteArray()))
                .ToList();
            return Task.FromResult(page);
        }

        public Task<bool> ReplaceWrapAsync(Guid id, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> replacement, string kekId, CancellationToken ct)
        {
            if (!Rows.TryGetValue(id, out var row) || row.Retired || !row.Wrapped.AsSpan().SequenceEqual(expected.Span))
                return Task.FromResult(false);
            Rows[id] = (row.Scope, replacement.ToArray(), kekId, false);
            return Task.FromResult(true);
        }
    }

    private sealed class MemoryAudit : IKekAuditSink
    {
        public readonly List<(string Kind, string Payload)> Events = [];

        public Task RecordAsync(string kind, string payloadJson, CancellationToken ct)
        {
            Events.Add((kind, payloadJson));
            return Task.CompletedTask;
        }
    }

    private FileKekProvider NewFileKek() => FileKekProvider.Open(Path.Combine(_root, "keys", "kek.bin"));

    private DpapiKekProvider NewDpapiKek() =>
        DpapiKekProvider.Open(Path.Combine(_root, "keys", "kek-dpapi.bin"), new TestProtector(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public async Task GetOrCreate_makes_one_dek_per_scope_and_reuses_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kek = NewFileKek();
        var store = new MemoryDekStore();
        var service = new SoulDekService(kek, store);

        using var first = await service.GetOrCreateAsync("soul-1", ct);
        using var again = await service.GetOrCreateAsync("soul-1", ct);
        using var other = await service.GetOrCreateAsync("install", ct);

        Assert.Equal(first.DekId, again.DekId);
        Assert.True(first.Key.Span.SequenceEqual(again.Key.Span));
        Assert.NotEqual(first.DekId, other.DekId);
        Assert.Equal(32, first.Key.Length);
        Assert.Equal(2, store.Rows.Count);
        Assert.All(store.Rows.Values, r => Assert.Equal(kek.KekId, r.KekId));
    }

    [Fact]
    public async Task A_dek_wrap_is_bound_to_its_own_id()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kek = NewFileKek();
        var store = new MemoryDekStore();
        var service = new SoulDekService(kek, store);
        using var a = await service.GetOrCreateAsync("soul-a", ct);
        using var b = await service.GetOrCreateAsync("soul-b", ct);

        // Move A's wrap onto B's row: it must not open there.
        store.Rows[b.DekId] = store.Rows[b.DekId] with { Wrapped = store.Rows[a.DekId].Wrapped };

        await Assert.ThrowsAsync<KekUnwrapException>(() => service.OpenAsync("soul-b", ct));
    }

    [Fact]
    public async Task Shred_makes_the_dek_unreadable_audits_it_and_a_new_dek_follows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kek = NewFileKek();
        var store = new MemoryDekStore();
        var audit = new MemoryAudit();
        var service = new SoulDekService(kek, store, audit);
        using var before = await service.GetOrCreateAsync("soul-1", ct);

        Assert.True(await service.ShredAsync("soul-1", ct));
        Assert.Null(await service.OpenAsync("soul-1", ct));
        Assert.Null(store.Rows[before.DekId].Wrapped);
        Assert.False(await service.ShredAsync("soul-1", ct));
        var evt = Assert.Single(audit.Events);
        Assert.Equal(SoulDekService.ShreddedAuditKind, evt.Kind);
        Assert.Contains(before.DekId.ToString(), evt.Payload);

        using var after = await service.GetOrCreateAsync("soul-1", ct);
        Assert.NotEqual(before.DekId, after.DekId);
    }

    [Fact]
    public async Task Rewrap_walks_every_store_records_the_new_kek_id_and_keeps_the_dek_key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = NewFileKek();
        using var target = NewDpapiKek();
        var deks = new MemoryDekStore();
        var persons = new MemoryDekStore();
        var oldService = new SoulDekService(source, deks);
        using var keep = await oldService.GetOrCreateAsync("soul-1", ct);
        var keptKey = keep.Key.ToArray();
        using var install = await oldService.GetOrCreateAsync("install", ct);
        var personId = Guid.CreateVersion7();
        persons.Rows[personId] = ("person", await source.WrapAsync(RandomNumberGenerator.GetBytes(32), personId.ToByteArray(), ct), source.KekId, false);

        var report = await KekRewriter.RunAsync(source, target, [persons, deks], new MemoryAudit(), batchSize: 1, ct: ct);

        Assert.True(report.Succeeded);
        Assert.Equal(3, report.Rewrapped);
        Assert.All(deks.Rows.Values, r => Assert.Equal(target.KekId, r.KekId));
        using var reopened = await new SoulDekService(target, deks).OpenAsync("soul-1", ct);
        Assert.NotNull(reopened);
        Assert.True(reopened.Key.Span.SequenceEqual(keptKey));
    }

    [Fact]
    public async Task Rewrap_skips_shredded_deks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = NewFileKek();
        using var target = NewDpapiKek();
        var deks = new MemoryDekStore();
        var service = new SoulDekService(source, deks);
        using var gone = await service.GetOrCreateAsync("soul-gone", ct);
        using var live = await service.GetOrCreateAsync("soul-live", ct);
        await service.ShredAsync("soul-gone", ct);

        var report = await KekRewriter.RunAsync(source, target, [deks], new MemoryAudit(), ct: ct);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Scanned);
        Assert.Equal(1, report.Rewrapped);
    }
    [Fact]
    public async Task The_secrets_adapter_wraps_under_the_one_host_kek_and_refuses_a_foreign_key_id()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kek = NewDpapiKek();
        var adapter = new SecretsKekAdapter(kek);
        var aad = RandomNumberGenerator.GetBytes(16);
        var key = RandomNumberGenerator.GetBytes(32);

        var wrapped = await adapter.WrapAsync(key, aad, ct);

        Assert.Equal(kek.KekId, adapter.CurrentKeyId);
        // The same wrap opens through the host KEK directly: there is one custody path.
        Assert.True((await kek.UnwrapAsync(wrapped, aad, ct)).AsSpan().SequenceEqual(key));
        Assert.True((await adapter.UnwrapAsync(kek.KekId, wrapped, aad, ct)).Span.SequenceEqual(key));
        await Assert.ThrowsAsync<CryptographicException>(async () => await adapter.UnwrapAsync("other-kek", wrapped, aad, ct));
        await Assert.ThrowsAsync<CryptographicException>(async () => await adapter.UnwrapAsync(kek.KekId, wrapped, RandomNumberGenerator.GetBytes(16), ct));
    }
}
