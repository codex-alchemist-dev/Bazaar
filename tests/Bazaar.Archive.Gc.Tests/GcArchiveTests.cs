using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Archive.Gc.Tests;

public sealed class GcArchiveTests : IDisposable
{
    private static readonly GcPrincipal _owner = new(Guid.NewGuid(), PrincipalKind.Owner, "Owner");
    private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-archive-gc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeReader(Dictionary<Guid, int> counts) : IGcLogReader
    {
        public async IAsyncEnumerable<GcEvent> ReadAsync(Guid gcId, long afterSeq, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
        {
            for (var s = afterSeq + 1; s <= Math.Min(counts[gcId], afterSeq + limit); s++)
            {
                yield return new GcEvent(gcId, s, Guid.NewGuid(), s % 2 == 0 ? GcEventKinds.System : GcEventKinds.Message, AuthorKind.Owner, _owner.Id,
                    SourceTrust.Owner, false, null, s % 2 == 0 ? null : Guid.Parse("00000000-0000-0000-0000-" + s.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)), s % 2 == 0 ? null : 1, null, null, null, _t0.AddMinutes(s));
                await Task.Yield();
            }
        }

        public Task<long> GetHeadSeqAsync(Guid gcId, CancellationToken ct) => Task.FromResult((long)counts[gcId]);
    }

    private sealed class FakeBridge(HashSet<Guid>? thinking = null) : IGcArchiveBridge
    {
        public List<Guid> Archived { get; } = [];

        public Task<bool> IsThinkingGcAsync(Guid gcId, CancellationToken ct) => Task.FromResult(thinking?.Contains(gcId) ?? false);

        public Task<string?> GetMessageTextAsync(Guid gcId, Guid messageId, int version, CancellationToken ct) => Task.FromResult<string?>("text " + messageId);

        public Task MarkGcArchivedAsync(Guid gcId, string rootHash, CancellationToken ct)
        {
            Archived.Add(gcId);
            return Task.CompletedTask;
        }
    }

    private sealed class Lister(params Guid[] ids) : IProjectGcLister
    {
        public Task<IReadOnlyList<Guid>> ListGcIdsAsync(string projectId, CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>(ids);
    }

    [Fact]
    public async Task A_gc_is_archived_across_pages_and_marked()
    {
        var gc = Guid.NewGuid();
        var bridge = new FakeBridge();
        var source = new GcArchiveSource(new FakeReader(new() { [gc] = 5 }), bridge, _owner, pageSize: 2);
        var manifest = await new ColdArchiver(source, new DirectoryArchiveStore(_root), null, new ArchiveOptions(), TimeProvider.System)
            .ArchiveAsync(GcArchiveUnits.ForGc(gc), TestContext.Current.CancellationToken);

        Assert.Equal(5, manifest.RecordCount);
        Assert.Equal([gc], bridge.Archived);
    }

    [Fact]
    public async Task A_thinking_gc_is_refused_and_not_marked()
    {
        var gc = Guid.NewGuid();
        var bridge = new FakeBridge([gc]);
        var source = new GcArchiveSource(new FakeReader(new() { [gc] = 3 }), bridge, _owner);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ColdArchiver(source, new DirectoryArchiveStore(_root), null, new ArchiveOptions(), TimeProvider.System)
                .ArchiveAsync(GcArchiveUnits.ForGc(gc), TestContext.Current.CancellationToken));
        Assert.Empty(bridge.Archived);
    }

    [Fact]
    public async Task A_project_archives_all_its_gcs_with_prefixed_record_ids()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var bridge = new FakeBridge();
        var gcSource = new GcArchiveSource(new FakeReader(new() { [a] = 2, [b] = 3 }), bridge, _owner);
        var index = new InMemoryArchiveSearchIndex();
        var manifest = await new ColdArchiver(new ProjectArchiveSource(new Lister(a, b), gcSource), new DirectoryArchiveStore(_root), index, new ArchiveOptions(), TimeProvider.System)
            .ArchiveAsync(GcArchiveUnits.ForProject("proj-x"), TestContext.Current.CancellationToken);

        Assert.Equal(5, manifest.RecordCount);
        Assert.Equal([a, b], bridge.Archived);
        var hits = await index.SearchAsync("text", 10, TestContext.Current.CancellationToken);
        Assert.All(hits, h => Assert.Contains("/seq-", h.RecordId));
    }

    [Fact]
    public async Task A_non_gc_unit_is_rejected()
    {
        var source = new GcArchiveSource(new FakeReader([]), new FakeBridge(), _owner);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var unused in source.ReadAsync(new ArchiveUnit(ArchiveUnitKind.Project, "x"), TestContext.Current.CancellationToken))
            {
                _ = unused;
            }
        });
    }
}
