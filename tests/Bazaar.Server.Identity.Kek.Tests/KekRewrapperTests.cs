using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.Identity.Kek.Tests;

/// <summary>The KEK re-wrap: move, resume after a crash, verify before replace, and audit. Database-free.</summary>
public sealed class KekRewrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-rewrap-" + Guid.CreateVersion7().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>An in-memory store with compare-and-set; can fail after N replacements to simulate a crash.</summary>
    private sealed class MemoryStore : IKekRewrapStore
    {
        public readonly List<KekWrapRecord> Rows = [];
        public int? FailAfterReplacements;
        public Guid? ForceConflictOn;
        private int _replacements;

        public Task<IReadOnlyList<KekWrapRecord>> ListLiveWrapsAsync(Guid? afterId, int limit, CancellationToken ct)
        {
            IReadOnlyList<KekWrapRecord> page = Rows
                .Where(r => afterId is null || r.Id.CompareTo(afterId.Value) > 0)
                .OrderBy(r => r.Id)
                .Take(limit)
                .ToList();
            return Task.FromResult(page);
        }

        public Task<bool> ReplaceWrapAsync(Guid id, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> replacement, string kekId, CancellationToken ct)
        {
            if (FailAfterReplacements is { } limit && _replacements >= limit)
                throw new IOException("simulated crash during replacement");
            var index = Rows.FindIndex(r => r.Id == id);
            if (index < 0 || ForceConflictOn == id || !Rows[index].Wrapped.AsSpan().SequenceEqual(expected.Span))
                return Task.FromResult(false);
            Rows[index] = Rows[index] with { Wrapped = replacement.ToArray() };
            _replacements++;
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

    private static async Task<MemoryStore> SeedAsync(FileKekProvider source, int rows, CancellationToken ct)
    {
        var store = new MemoryStore();
        for (var i = 0; i < rows; i++)
        {
            var id = Guid.CreateVersion7();
            var dek = RandomNumberGenerator.GetBytes(32);
            store.Rows.Add(new KekWrapRecord(id, await source.WrapAsync(dek, id.ToByteArray(), ct), id.ToByteArray()));
        }

        return store;
    }

    private static async Task AssertAllOpenUnderAsync(DpapiKekProvider kek, MemoryStore store, CancellationToken ct)
    {
        foreach (var row in store.Rows)
        {
            var plain = await kek.UnwrapAsync(row.Wrapped, row.AssociatedData, ct);
            Assert.Equal(32, plain.Length);
        }
    }

    [Fact]
    public async Task Moves_every_live_wrap_to_the_target_and_reports_success()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = FileKekProvider.Open(Path.Combine(_root, "keys", "kek.bin"));
        using var target = DpapiKekProvider.Open(Path.Combine(_root, "keys", "kek-dpapi.bin"), TestProtector.Create());
        var store = await SeedAsync(source, rows: 5, ct);
        var audit = new MemoryAudit();

        var report = await KekRewriter.RunAsync(source, target, store, audit, batchSize: 2, ct: ct);

        Assert.True(report.Succeeded);
        Assert.Equal(5, report.Scanned);
        Assert.Equal(5, report.Rewrapped);
        Assert.Equal(0, report.AlreadyCurrent);
        await AssertAllOpenUnderAsync(target, store, ct);
        Assert.Equal([KekRewriter.StartedAuditKind, KekRewriter.CompletedAuditKind], audit.Events.Select(e => e.Kind));
        Assert.Contains("\"rewrapped\":5", audit.Events[1].Payload);
    }

    [Fact]
    public async Task A_rerun_after_success_moves_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = FileKekProvider.Open(Path.Combine(_root, "keys", "kek.bin"));
        using var target = DpapiKekProvider.Open(Path.Combine(_root, "keys", "kek-dpapi.bin"), TestProtector.Create());
        var store = await SeedAsync(source, rows: 3, ct);
        await KekRewriter.RunAsync(source, target, store, new MemoryAudit(), ct: ct);

        var rerun = await KekRewriter.RunAsync(source, target, store, new MemoryAudit(), ct: ct);

        Assert.True(rerun.Succeeded);
        Assert.Equal(0, rerun.Rewrapped);
        Assert.Equal(3, rerun.AlreadyCurrent);
    }

    [Fact]
    public async Task A_crash_mid_run_leaves_every_row_openable_and_a_rerun_finishes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = FileKekProvider.Open(Path.Combine(_root, "keys", "kek.bin"));
        using var target = DpapiKekProvider.Open(Path.Combine(_root, "keys", "kek-dpapi.bin"), TestProtector.Create());
        var store = await SeedAsync(source, rows: 6, ct);
        store.FailAfterReplacements = 2;

        await Assert.ThrowsAsync<IOException>(() => KekRewriter.RunAsync(source, target, store, new MemoryAudit(), batchSize: 4, ct: ct));

        // Every row opens under exactly one of the two KEKs: the replaced ones under target, the rest still under source.
        foreach (var row in store.Rows)
        {
            var underSource = await OpensAsync(source, row, ct);
            var underTarget = await OpensAsync(target, row, ct);
            Assert.True(underSource ^ underTarget, "A row must open under exactly one KEK after a crash.");
        }

        store.FailAfterReplacements = null;
        var rerun = await KekRewriter.RunAsync(source, target, store, new MemoryAudit(), batchSize: 4, ct: ct);

        Assert.True(rerun.Succeeded);
        Assert.Equal(2, rerun.AlreadyCurrent);
        Assert.Equal(4, rerun.Rewrapped);
        await AssertAllOpenUnderAsync(target, store, ct);
    }

    [Fact]
    public async Task A_row_that_opens_under_neither_kek_is_reported_and_leaves_the_run_incomplete()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = FileKekProvider.Open(Path.Combine(_root, "keys", "kek.bin"));
        using var target = DpapiKekProvider.Open(Path.Combine(_root, "keys", "kek-dpapi.bin"), TestProtector.Create());
        var store = await SeedAsync(source, rows: 2, ct);
        var stranger = store.Rows[0];
        store.Rows[0] = stranger with { Wrapped = RandomNumberGenerator.GetBytes(60) };

        var report = await KekRewriter.RunAsync(source, target, store, new MemoryAudit(), ct: ct);

        Assert.False(report.Succeeded);
        Assert.Equal([stranger.Id], report.Failed);
        Assert.Equal(1, report.Rewrapped);
    }

    [Fact]
    public async Task A_compare_and_set_that_loses_a_race_is_a_conflict_not_a_success()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = FileKekProvider.Open(Path.Combine(_root, "keys", "kek.bin"));
        using var target = DpapiKekProvider.Open(Path.Combine(_root, "keys", "kek-dpapi.bin"), TestProtector.Create());
        var store = await SeedAsync(source, rows: 2, ct);
        store.ForceConflictOn = store.Rows[1].Id;

        var report = await KekRewriter.RunAsync(source, target, store, new MemoryAudit(), ct: ct);

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.Conflicts);
        Assert.Equal(1, report.Rewrapped);
    }

    [Fact]
    public async Task Refuses_a_source_and_target_that_are_the_same_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = FileKekProvider.Open(Path.Combine(_root, "keys", "kek.bin"));
        using var other = FileKekProvider.Open(Path.Combine(_root, "keys", "kek.bin"));

        await Assert.ThrowsAsync<ArgumentException>(() => KekRewriter.RunAsync(source, other, new MemoryStore(), new MemoryAudit(), ct: ct));
    }

    private static async Task<bool> OpensAsync(IKekProvider kek, KekWrapRecord row, CancellationToken ct)
    {
        try
        {
            await kek.UnwrapAsync(row.Wrapped, row.AssociatedData, ct);
            return true;
        }
        catch (KekUnwrapException)
        {
            return false;
        }
    }
}
