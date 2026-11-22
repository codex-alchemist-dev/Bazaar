using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using static Bazaar.Server.Portability.Tests.GcFixtures;

namespace Bazaar.Server.Portability.Tests;

public class GcJsonlExporterTests
{
    private static string PathOf(string file) => GcJsonlFormat.PathFor(GcId, file);

    private static JsonElement Parse(string line) => JsonElement.Parse(line);

    private static GcJsonlExporter Exporter(FakeGcLog log, FakeVersionSource versions, int pageSize = 500) =>
        new(log, versions, pageSize);

    private static async Task<(GcExportResult Result, InMemoryExportFileStore Store)> ExportAsync(
        FakeGcLog log, FakeVersionSource versions, int pageSize = 500)
    {
        var store = new InMemoryExportFileStore();
        var result = await Exporter(log, versions, pageSize).ExportAsync(GcId, Owner, store, CancellationToken.None);
        return (result, store);
    }

    [Fact]
    public async Task Events_are_exported_in_seq_order_with_a_gap_free_seq()
    {
        var log = new FakeGcLog();
        log.Events.Add(Event(1, GcEventKinds.System));
        log.Events.Add(Event(2, GcEventKinds.System));
        log.Events.Add(Event(3, GcEventKinds.System));

        // A page of two forces a second read, so paging is part of the check.
        var (result, store) = await ExportAsync(log, new FakeVersionSource(), pageSize: 2);

        var seqs = store.Lines(PathOf(GcJsonlFormat.EventsFile)).Select(l => Parse(l).GetProperty("seq").GetInt64()).ToArray();
        Assert.Equal("1,2,3", string.Join(",", seqs));
        Assert.Equal(3, result.HeadSeq);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task Every_edited_version_is_exported_in_order_with_the_edited_marker()
    {
        var log = new FakeGcLog();
        log.Events.Add(MessageEvent(1, 1, 1));
        log.Events.Add(MessageEvent(2, 1, 2));
        log.Events.Add(MessageEvent(3, 1, 3));
        var versions = new FakeVersionSource();
        versions.Rows.Add(Sealed(1, 1, 1, "first"));
        versions.Rows.Add(Sealed(2, 1, 2, "second"));
        versions.Rows.Add(Sealed(3, 1, 3, "third"));

        var (result, store) = await ExportAsync(log, versions);

        var rows = store.Lines(PathOf(GcJsonlFormat.VersionsFile)).Select(Parse).ToArray();
        Assert.Equal("1,2,3", string.Join(",", rows.Select(r => r.GetProperty("version").GetInt32())));
        Assert.Equal("False,True,True", string.Join(",", rows.Select(r => r.GetProperty("editMarker").GetBoolean())));
        Assert.Equal(Convert.ToBase64String(Seal("second")), rows[1].GetProperty("body").GetProperty("ciphertext").GetString());

        // One message, first seen at seq 1; its later versions are in versions.jsonl, not repeated here.
        var messages = store.Lines(PathOf(GcJsonlFormat.MessagesFile));
        Assert.Single(messages);
        Assert.Equal(1, Parse(messages[0]).GetProperty("firstSeq").GetInt64());

        using var head = JsonDocument.Parse(store.Text(PathOf(GcJsonlFormat.HeadFile)));
        Assert.Equal(3, head.RootElement.GetProperty("versionCount").GetInt64());
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task Tombstones_are_exported_as_tombstones_without_a_body()
    {
        var log = new FakeGcLog();
        log.Events.Add(MessageEvent(1, 1, 1));
        log.Events.Add(Event(2, GcEventKinds.System, MessageId(1), 2));
        var versions = new FakeVersionSource();
        versions.Rows.Add(Sealed(1, 1, 1, "kept until erased"));
        versions.Rows.Add(Tombstone(2, 1, 2));

        var (result, store) = await ExportAsync(log, versions);

        var tombstone = Parse(store.Lines(PathOf(GcJsonlFormat.VersionsFile))[1]);
        Assert.True(tombstone.GetProperty("tombstone").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tombstone.GetProperty("body").ValueKind);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task A_tombstone_that_still_carries_a_body_is_refused()
    {
        var log = new FakeGcLog();
        log.Events.Add(MessageEvent(1, 1, 1));
        var versions = new FakeVersionSource();
        versions.Rows.Add(Tombstone(1, 1, 1) with { Ciphertext = Seal("leftover") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Exporter(log, versions).ExportAsync(GcId, Owner, new InMemoryExportFileStore(), CancellationToken.None));
    }

    [Fact]
    public async Task Bodies_are_copied_as_ciphertext_and_never_decrypted()
    {
        const string plaintext = "the owner's private note";
        var log = new FakeGcLog();
        log.Events.Add(MessageEvent(1, 1, 1));
        var versions = new FakeVersionSource();
        versions.Rows.Add(Sealed(1, 1, 1, plaintext));

        var (_, store) = await ExportAsync(log, versions);

        var everything = string.Concat(store.Files.Keys.Select(store.Text));
        Assert.DoesNotContain(plaintext, everything, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Seal(plaintext)), everything, StringComparison.Ordinal);
        Assert.Contains(Key.ToString("D"), everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_log_gives_the_same_bytes()
    {
        var log = new FakeGcLog();
        log.Events.Add(MessageEvent(1, 1, 1));
        log.Events.Add(Event(2, GcEventKinds.System, payload: "{\"zeta\":1,\"alpha\":{\"d\":true,\"c\":[2,1]}}"));
        log.Events.Add(MessageEvent(3, 1, 2));
        log.Events.Add(MessageEvent(4, 2, 1));
        var versions = new FakeVersionSource();
        versions.Rows.Add(Sealed(1, 1, 1, "a"));
        versions.Rows.Add(Sealed(3, 1, 2, "b"));
        versions.Rows.Add(Sealed(4, 2, 1, "c"));

        var (_, first) = await ExportAsync(log, versions);
        var (_, second) = await ExportAsync(log, versions);

        Assert.Equal(first.Files.Keys.OrderBy(k => k, StringComparer.Ordinal), second.Files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var path in first.Files.Keys)
        {
            Assert.Equal(first.Get(path), second.Get(path));
        }
    }

    [Fact]
    public async Task Payload_keys_are_sorted_so_the_bytes_are_canonical()
    {
        var log = new FakeGcLog();
        log.Events.Add(Event(1, GcEventKinds.System, payload: "{\"zeta\":1,\"alpha\":{\"d\":true,\"c\":[2,1]}}"));

        var (_, store) = await ExportAsync(log, new FakeVersionSource());

        Assert.Contains("\"payload\":{\"alpha\":{\"c\":[2,1],\"d\":true},\"zeta\":1}", store.Text(PathOf(GcJsonlFormat.EventsFile)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_committed_after_the_head_are_left_out()
    {
        var log = new FakeGcLog();
        log.Events.Add(MessageEvent(1, 1, 1));
        log.Events.Add(MessageEvent(2, 2, 1));
        log.Events.Add(MessageEvent(3, 3, 1));
        log.HeadOverride = 2;
        var versions = new FakeVersionSource();
        versions.Rows.Add(Sealed(1, 1, 1, "a"));
        versions.Rows.Add(Sealed(2, 2, 1, "b"));
        versions.Rows.Add(Sealed(3, 3, 1, "c"));

        var (result, store) = await ExportAsync(log, versions);

        Assert.Equal(2, result.HeadSeq);
        Assert.Equal(2, store.Lines(PathOf(GcJsonlFormat.EventsFile)).Length);
        Assert.Equal(2, store.Lines(PathOf(GcJsonlFormat.VersionsFile)).Length);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task A_grant_that_stops_early_is_reported_as_incomplete()
    {
        var log = new FakeGcLog();
        log.Events.Add(Event(1, GcEventKinds.System));
        log.Events.Add(Event(2, GcEventKinds.System));
        log.Events.Add(Event(3, GcEventKinds.System));
        log.GrantThroughSeq = 2;

        var (result, _) = await ExportAsync(log, new FakeVersionSource());

        Assert.False(result.Complete);
        Assert.Contains(result.SelfCheck.Problems, p => p.Contains("ends at seq 2; head.json says the head is seq 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_gc_exports_and_passes_the_self_check()
    {
        var (result, store) = await ExportAsync(new FakeGcLog(), new FakeVersionSource());

        Assert.Equal(0, result.HeadSeq);
        Assert.True(result.Complete);
        Assert.Empty(store.Get(PathOf(GcJsonlFormat.EventsFile)));
        using var head = JsonDocument.Parse(store.Text(PathOf(GcJsonlFormat.HeadFile)));
        Assert.Equal(GcJsonlFormat.GenesisHash, head.RootElement.GetProperty("eventHeadHash").GetString());
    }

    [Fact]
    public async Task A_viewer_without_access_is_refused()
    {
        var log = new FakeGcLog { Denied = true };
        log.Events.Add(Event(1, GcEventKinds.System));

        await Assert.ThrowsAsync<GcAccessDeniedException>(() => ExportAsync(log, new FakeVersionSource()));
    }

    [Fact]
    public async Task A_reader_that_repeats_a_seq_is_refused_rather_than_looping()
    {
        var log = new FakeGcLog();
        log.Events.Add(Event(1, GcEventKinds.System));
        log.Events.Add(Event(1, GcEventKinds.System));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportAsync(log, new FakeVersionSource()));
    }

    [Fact]
    public async Task Every_line_carries_a_hash_chain_that_starts_at_the_genesis_hash()
    {
        var log = new FakeGcLog();
        log.Events.Add(MessageEvent(1, 1, 1));
        log.Events.Add(Event(2, GcEventKinds.System));

        var (_, store) = await ExportAsync(log, new FakeVersionSource());

        var lines = store.Lines(PathOf(GcJsonlFormat.EventsFile));
        Assert.Equal(GcJsonlFormat.GenesisHash, Parse(lines[0]).GetProperty("prevHash").GetString());
        Assert.Equal(Parse(lines[0]).GetProperty("hash").GetString(), Parse(lines[1]).GetProperty("prevHash").GetString());
        var bytes = store.Get(PathOf(GcJsonlFormat.EventsFile));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
    }
}
