using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Archive.Tests;

public sealed class ArchiveTests : IDisposable
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ArchiveUnit _unit = new(ArchiveUnitKind.GroupChat, "gc-alpha");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-archive-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static List<ArchiveRecord> Records(int n, string kind = "gc_message") =>
        Enumerable.Range(1, n).Select(i => new ArchiveRecord("m" + i, kind, _t0.AddMinutes(i), $"hello \"world\"\nline {i} needle{i % 2}")).ToList();

    private sealed class FakeSource(List<ArchiveRecord> records) : IArchiveSource
    {
        public List<string> Marked { get; } = [];

        public async IAsyncEnumerable<ArchiveRecord> ReadAsync(ArchiveUnit unit, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var r in records)
            {
                yield return r;
                await Task.Yield();
            }
        }

        public Task MarkArchivedAsync(ArchiveUnit unit, string rootHash, CancellationToken ct)
        {
            Marked.Add(rootHash);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTarget : IArchiveRestoreTarget
    {
        public Dictionary<string, ArchiveRecord> Live { get; } = [];

        public Task<string?> GetLiveHashAsync(ArchiveUnit unit, string recordId, CancellationToken ct) =>
            Task.FromResult(Live.TryGetValue(recordId, out var r) ? ArchiveHashing.RecordHash(r) : null);

        public Task WriteAsync(ArchiveUnit unit, ArchiveRecord record, CancellationToken ct)
        {
            Live[record.Id] = record;
            return Task.CompletedTask;
        }
    }

    private static ColdArchiver Archiver(FakeSource src, DirectoryArchiveStore store, IArchiveSearchIndex? index = null, int seg = 2) =>
        new(src, store, index, new ArchiveOptions(seg), TimeProvider.System);

    [Fact]
    public async Task Archive_then_restore_round_trips_byte_identical_records()
    {
        var records = Records(5);
        var store = new DirectoryArchiveStore(_root);
        var src = new FakeSource(records);
        var manifest = await Archiver(src, store).ArchiveAsync(_unit, TestContext.Current.CancellationToken);

        Assert.Equal(3, manifest.Segments.Count);
        Assert.Equal(5, manifest.RecordCount);
        Assert.Equal([manifest.RootHash], src.Marked);

        var target = new FakeTarget();
        var result = await new ArchiveRestorer(store, target).RestoreAsync(_unit, TestContext.Current.CancellationToken);
        Assert.Equal(5, result.Restored);
        Assert.Empty(result.Conflicts);
        Assert.Equal(records, target.Live.Values.OrderBy(r => r.At).ToList());
    }

    [Fact]
    public async Task Same_input_gives_the_same_root_hash()
    {
        var a = await Archiver(new FakeSource(Records(5)), new DirectoryArchiveStore(_root + "-a")).ArchiveAsync(_unit, TestContext.Current.CancellationToken);
        var b = await Archiver(new FakeSource(Records(5)), new DirectoryArchiveStore(_root + "-b")).ArchiveAsync(_unit, TestContext.Current.CancellationToken);
        Assert.Equal(a.RootHash, b.RootHash);
        Directory.Delete(_root + "-a", true);
        Directory.Delete(_root + "-b", true);
    }

    [Fact]
    public async Task A_flipped_byte_fails_verify_and_blocks_restore()
    {
        var store = new DirectoryArchiveStore(_root);
        await Archiver(new FakeSource(Records(3)), store).ArchiveAsync(_unit, TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, "groupchat", "gc-alpha", "segments", "0001.jsonl");
        var bytes = File.ReadAllBytes(path);
        bytes[5] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        var verify = await new ArchiveVerifier(store).VerifyAsync(_unit, TestContext.Current.CancellationToken);
        Assert.False(verify.Ok);
        Assert.Contains(verify.Problems, p => p.Contains("0001.jsonl"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ArchiveRestorer(store, new FakeTarget()).RestoreAsync(_unit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_or_extra_segment_fails_verify()
    {
        var store = new DirectoryArchiveStore(_root);
        await Archiver(new FakeSource(Records(3)), store).ArchiveAsync(_unit, TestContext.Current.CancellationToken);
        await store.PutAsync(_unit, "segments/0099.jsonl", Encoding.UTF8.GetBytes("x"), TestContext.Current.CancellationToken);
        Assert.Contains("unlisted file segments/0099.jsonl", (await new ArchiveVerifier(store).VerifyAsync(_unit, TestContext.Current.CancellationToken)).Problems);
        File.Delete(Path.Combine(_root, "groupchat", "gc-alpha", "segments", "0002.jsonl"));
        Assert.Contains("segment segments/0002.jsonl missing", (await new ArchiveVerifier(store).VerifyAsync(_unit, TestContext.Current.CancellationToken)).Problems);
    }

    [Fact]
    public async Task A_missing_manifest_fails_closed()
    {
        var verify = await new ArchiveVerifier(new DirectoryArchiveStore(_root)).VerifyAsync(_unit, TestContext.Current.CancellationToken);
        Assert.False(verify.Ok);
    }

    [Fact]
    public async Task Thinking_records_are_refused_and_the_source_stays_unmarked()
    {
        var records = Records(2);
        records.Add(new ArchiveRecord("t1", "thinking", _t0, "secret thoughts"));
        var src = new FakeSource(records);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Archiver(src, new DirectoryArchiveStore(_root)).ArchiveAsync(_unit, TestContext.Current.CancellationToken));
        Assert.Empty(src.Marked);
    }

    [Fact]
    public async Task Archiving_twice_and_unsafe_ids_are_refused()
    {
        var store = new DirectoryArchiveStore(_root);
        var archiver = Archiver(new FakeSource(Records(1)), store);
        await archiver.ArchiveAsync(_unit, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => archiver.ArchiveAsync(_unit, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => archiver.ArchiveAsync(new ArchiveUnit(ArchiveUnitKind.Project, "../x"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Archived_units_are_found_by_search_without_restoring()
    {
        var index = new InMemoryArchiveSearchIndex();
        await Archiver(new FakeSource(Records(4)), new DirectoryArchiveStore(_root), index).ArchiveAsync(_unit, TestContext.Current.CancellationToken);
        var hits = await index.SearchAsync("NEEDLE1 hello", 10, TestContext.Current.CancellationToken);
        Assert.Equal(["m1", "m3"], hits.Select(h => h.RecordId).ToArray());
        Assert.All(hits, h => Assert.Equal(_unit, h.Unit));
        Assert.Empty(await index.SearchAsync("absent", 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_restarted_index_is_rebuilt_from_verified_bundles_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new DirectoryArchiveStore(_root);
        await Archiver(new FakeSource(Records(4)), store).ArchiveAsync(_unit, ct);
        var broken = new ArchiveUnit(ArchiveUnitKind.GroupChat, "gc-broken");
        await Archiver(new FakeSource(Records(2)), store).ArchiveAsync(broken, ct);
        var segment = Directory.EnumerateFiles(Path.Combine(_root), "0001.jsonl", SearchOption.AllDirectories).First(f => f.Contains("gc-broken"));
        File.AppendAllText(segment, " ");

        var fresh = new InMemoryArchiveSearchIndex();
        var rebuilder = new ArchiveIndexRebuilder(store, new ArchiveVerifier(store), fresh);
        await rebuilder.EnsureBuiltAsync(ct);
        await rebuilder.EnsureBuiltAsync(ct);

        var hits = await fresh.SearchAsync("needle1", 10, ct);
        Assert.Equal(["m1", "m3"], hits.Select(h => h.RecordId).ToArray());
        Assert.DoesNotContain(hits, h => h.Unit == broken);
    }

    [Fact]
    public async Task Restore_reports_a_conflict_instead_of_overwriting()
    {
        var store = new DirectoryArchiveStore(_root);
        await Archiver(new FakeSource(Records(3)), store).ArchiveAsync(_unit, TestContext.Current.CancellationToken);
        var target = new FakeTarget();
        target.Live["m1"] = Records(3)[0];
        target.Live["m2"] = new ArchiveRecord("m2", "gc_message", _t0, "changed since");

        var result = await new ArchiveRestorer(store, target).RestoreAsync(_unit, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(["m2"], result.Conflicts);
        Assert.Equal("changed since", target.Live["m2"].Body);
    }

    [Fact]
    public async Task The_store_lists_units_and_rejects_escaping_names()
    {
        var store = new DirectoryArchiveStore(_root);
        await Archiver(new FakeSource(Records(1)), store).ArchiveAsync(_unit, TestContext.Current.CancellationToken);
        Assert.Equal([_unit], await store.ListUnitsAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(_unit, "../../x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_component_registers_the_services_and_reads_settings()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Archive:Root"] = _root,
            ["Bazaar:Archive:SegmentMaxRecords"] = "7",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IArchiveSource>(new FakeSource([]));
        new ArchiveComponent().AddServices(services, config);
        using var sp = services.BuildServiceProvider();

        Assert.Equal(7, sp.GetRequiredService<ArchiveOptions>().SegmentMaxRecords);
        Assert.NotNull(sp.GetRequiredService<ColdArchiver>());
        Assert.Equal("archive", ArchiveComponent.ManifestDefinition.Id);
    }
}
