using System.Text;
using Bazaar.Contracts.Gc;
using static Bazaar.Server.Portability.Tests.GcFixtures;

namespace Bazaar.Server.Portability.Tests;

public class GcExportSelfCheckTests
{
    private static string PathOf(string file) => GcJsonlFormat.PathFor(GcId, file);

    /// <summary>A small GC with a message, its edit, a second message and a tombstone: every kind of line the check reads.</summary>
    private static async Task<InMemoryExportFileStore> SampleExportAsync()
    {
        var log = new FakeGcLog();
        log.Events.Add(MessageEvent(1, 1, 1));
        log.Events.Add(MessageEvent(2, 1, 2));
        log.Events.Add(MessageEvent(3, 2, 1));
        log.Events.Add(Event(4, GcEventKinds.System, MessageId(2), 2));
        var versions = new FakeVersionSource();
        versions.Rows.Add(Sealed(1, 1, 1, "one"));
        versions.Rows.Add(Sealed(2, 1, 2, "two"));
        versions.Rows.Add(Sealed(3, 2, 1, "three"));
        versions.Rows.Add(Tombstone(4, 2, 2));

        var store = new InMemoryExportFileStore();
        var result = await new GcJsonlExporter(log, versions).ExportAsync(GcId, Owner, store, CancellationToken.None);
        Assert.True(result.Complete, string.Join("; ", result.SelfCheck.Problems));
        return store;
    }

    private static byte[] Join(IEnumerable<string> lines) =>
        Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));

    private static Task<GcExportSelfCheckResult> CheckAsync(InMemoryExportFileStore store) =>
        GcExportSelfCheck.VerifyAsync(store, GcId, CancellationToken.None);

    [Fact]
    public async Task A_clean_export_passes()
    {
        var store = await SampleExportAsync();

        var result = await CheckAsync(store);

        Assert.True(result.Passed, string.Join("; ", result.Problems));
    }

    [Fact]
    public async Task A_dropped_event_is_caught()
    {
        var store = await SampleExportAsync();
        var lines = store.Lines(PathOf(GcJsonlFormat.EventsFile)).ToList();
        lines.RemoveAt(1);
        store.Replace(PathOf(GcJsonlFormat.EventsFile), Join(lines));

        var result = await CheckAsync(store);

        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("seq 3 found where seq 2 was expected", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("prevHash does not chain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reordered_event_is_caught()
    {
        var store = await SampleExportAsync();
        var lines = store.Lines(PathOf(GcJsonlFormat.EventsFile)).ToList();
        (lines[0], lines[1]) = (lines[1], lines[0]);
        store.Replace(PathOf(GcJsonlFormat.EventsFile), Join(lines));

        var result = await CheckAsync(store);

        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("seq 2 found where seq 1 was expected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_altered_line_fails_its_own_hash()
    {
        var store = await SampleExportAsync();
        var lines = store.Lines(PathOf(GcJsonlFormat.EventsFile)).ToList();
        lines[1] = lines[1].Replace("\"kind\":\"message_edit\"", "\"kind\":\"system\"", StringComparison.Ordinal);
        store.Replace(PathOf(GcJsonlFormat.EventsFile), Join(lines));

        var result = await CheckAsync(store);

        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("the hash does not match the line content", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("events.jsonl: sha256 does not match head.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_changed_version_file_fails_its_recorded_hash()
    {
        var store = await SampleExportAsync();
        var bytes = store.Get(PathOf(GcJsonlFormat.VersionsFile)).ToArray();
        bytes[^3] ^= 0x01;
        store.Replace(PathOf(GcJsonlFormat.VersionsFile), bytes);

        var result = await CheckAsync(store);

        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("versions.jsonl: sha256 does not match head.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_version_row_is_caught()
    {
        var store = await SampleExportAsync();
        var lines = store.Lines(PathOf(GcJsonlFormat.VersionsFile)).ToList();
        lines.RemoveAt(1);
        store.Replace(PathOf(GcJsonlFormat.VersionsFile), Join(lines));

        var result = await CheckAsync(store);

        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("seq 2: a message_edit event has no version row", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_message_line_that_does_not_match_its_event_is_caught()
    {
        var store = await SampleExportAsync();
        var lines = store.Lines(PathOf(GcJsonlFormat.MessagesFile)).ToList();
        lines.RemoveAt(0);
        store.Replace(PathOf(GcJsonlFormat.MessagesFile), Join(lines));

        var result = await CheckAsync(store);

        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("does not match the event", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_head_that_misstates_the_counts_is_caught()
    {
        var store = await SampleExportAsync();
        var head = GcExportHead.TryParse(store.Get(PathOf(GcJsonlFormat.HeadFile)), out _)!;
        store.Replace(PathOf(GcJsonlFormat.HeadFile), (head with { EventCount = 2 }).ToBytes());

        var result = await CheckAsync(store);

        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("has 4 events; head.json says 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_head_is_reported_not_thrown()
    {
        var store = await SampleExportAsync();
        store.Remove(PathOf(GcJsonlFormat.HeadFile));

        var result = await CheckAsync(store);

        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("head.json is missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_events_file_is_reported_not_thrown()
    {
        var store = await SampleExportAsync();
        store.Remove(PathOf(GcJsonlFormat.EventsFile));

        var result = await CheckAsync(store);

        Assert.False(result.Passed);
        Assert.Contains(result.Problems, p => p.Contains("events.jsonl is missing", StringComparison.Ordinal));
    }
}
