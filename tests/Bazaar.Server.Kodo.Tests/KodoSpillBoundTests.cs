using System.Text;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Probes.Self;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Kodo.Tests;

/// <summary>The spill size bound with its gap marker, the seal after a replay, and the red probe for an unreadable spill (doc 46 section 7.5).</summary>
public sealed class KodoSpillBoundTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kodo-spill-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string SpillPath => Path.Combine(_dir, "kodo-journal.spill");

    private static string ComponentState(KodoMonitor monitor, string id) => monitor.Latest!.Components.Single(c => c.Id == id).State;

    private static DateTimeOffset At(int second) => Make.Start.AddSeconds(second);

    [Fact]
    public void At_the_bound_the_newest_stay_and_the_gap_is_marked_as_an_observation()
    {
        using var journal = new KodoJournal(_dir, 10_000, maxSpillBytes: 4000);

        for (int i = 0; i < 60; i++)
        {
            journal.Append(At(i), "observation", $"probe host.disk.free.data green -> green {i}");
        }

        IReadOnlyList<JournalRecord> all = journal.ReadFrom(0, 1000);
        JournalRecord[] markers = [.. all.Where(r => Encoding.UTF8.GetString(r.Payload).StartsWith("journal gap spill_bound first=0 ", StringComparison.Ordinal))];
        Assert.NotEmpty(markers);
        Assert.Equal("observation", markers[0].Kind);
        Assert.Null(JournalChain.Verify(all).FirstBrokenIndex);
        Assert.Equal(all.Count, journal.NextSequence);
        Assert.True(new FileInfo(SpillPath).Length <= 4000);
    }

    [Fact]
    public void A_bounded_spill_reopens_with_the_chain_continuing_and_the_journal_writable()
    {
        long next;
        byte[]? head;
        using (var journal = new KodoJournal(_dir, 10_000, maxSpillBytes: 4000))
        {
            for (int i = 0; i < 60; i++)
            {
                journal.Append(At(i), "observation", $"entry {i}");
            }

            next = journal.NextSequence;
            head = journal.Head;
        }

        using (var reopened = new KodoJournal(_dir, 10_000, maxSpillBytes: 4000))
        {
            Assert.True(reopened.CheckWritable());
            Assert.True(reopened.CheckSpillReadable());
            Assert.Equal(next, reopened.NextSequence);
            Assert.Equal(head, reopened.Head);
            Assert.NotNull(reopened.Append(At(100), "observation", "after restart"));
        }

        Assert.True(JournalSpill.Read(SpillPath).Verify().IsValid);
    }

    [Fact]
    public async Task An_unreadable_spill_is_red_in_the_probe_never_green()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(SpillPath, "this is not a spill record\n", Ct);
        using var monitor = Make.Monitor(new FakeReadings(), Make.Clock(), options: new KodoServerOptions { DataDirectory = _dir });

        await monitor.RunCycleAsync(Ct);

        Assert.Equal("red", ComponentState(monitor, SpillAgeProbe.Id));
    }

    [Fact]
    public async Task A_spill_that_lost_unsent_records_to_the_bound_is_sealed_and_red()
    {
        // The first five records reach the store, then the bound drops more of the oldest than the store holds.
        List<JournalRecord> first;
        using (var early = new KodoJournal(_dir, 10_000, maxSpillBytes: 4000))
        {
            for (int i = 0; i < 80; i++)
            {
                early.Append(At(i), "observation", $"entry {i}");
            }

            first = [.. early.ReadFrom(0, 5)];
        }

        var store = new FakeJournalStore();
        store.Records.AddRange(first);
        using var journal = new KodoJournal(_dir, 10_000, maxSpillBytes: 4000);
        Assert.True(journal.ReadFrom(0, 1)[0].Sequence > 5);
        var sync = new KodoJournalSync(journal, store, new KodoServerOptions(), Make.Clock(), NullLogger.Instance);

        await sync.SyncAsync(null, Ct);

        Assert.True(sync.Blocked);
        Assert.True(sync.ReadSpill()!.Value.Sealed);
        Assert.Equal(5, store.Records.Count);
    }

    [Fact]
    public async Task After_an_outage_is_replayed_the_spill_is_sealed_and_the_chain_carries_on()
    {
        var time = Make.Clock();
        var readings = new FakeReadings();
        var store = new FakeJournalStore();
        var options = new KodoServerOptions { DataDirectory = _dir };
        int sealedBefore;
        using (var monitor = Make.Monitor(readings, time, options: options, store: store))
        {
            await monitor.RunCycleAsync(Ct);
            sealedBefore = SealedFiles().Length;

            store.Down = true;
            readings.DiskUsedPercent = 97;
            time.Advance(TimeSpan.FromMinutes(1));
            await monitor.RunCycleAsync(Ct);
            Assert.Equal(sealedBefore, SealedFiles().Length);

            store.Down = false;
            readings.DiskUsedPercent = 40;
            time.Advance(TimeSpan.FromMinutes(1));
            await monitor.RunCycleAsync(Ct);

            Assert.Equal(sealedBefore + 1, SealedFiles().Length);
            Assert.Equal("green", ComponentState(monitor, SpillAgeProbe.Id));
            Assert.Equal("green", ComponentState(monitor, JournalWritableProbe.Id));
            Assert.Null(JournalChain.Verify(store.Records).FirstBrokenIndex);

            // Steady state: no further backlog, so no further seal.
            time.Advance(TimeSpan.FromMinutes(1));
            await monitor.RunCycleAsync(Ct);
            Assert.Equal(sealedBefore + 1, SealedFiles().Length);
        }

        JournalSpillRead active = JournalSpill.Read(SpillPath);
        Assert.NotNull(active.Anchor);
        Assert.True(active.Verify().IsValid);
        Assert.True(JournalSpill.Read(SealedFiles().Single()).Verify().IsValid);

        using var restarted = Make.Monitor(new FakeReadings(), time, options: options, store: store);
        await restarted.RunCycleAsync(Ct);
        Assert.Null(JournalChain.Verify(store.Records).FirstBrokenIndex);
        Assert.Equal("green", ComponentState(restarted, JournalWritableProbe.Id));
    }

    [Fact]
    public async Task A_healthy_database_never_seals()
    {
        var time = Make.Clock();
        var store = new FakeJournalStore();
        using var monitor = Make.Monitor(new FakeReadings(), time, options: new KodoServerOptions { DataDirectory = _dir }, store: store);

        for (int i = 0; i < 3; i++)
        {
            await monitor.RunCycleAsync(Ct);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Empty(SealedFiles());
    }

    [Fact]
    public void Sealed_archives_older_than_the_retention_window_are_deleted_on_the_next_seal()
    {
        using var journal = new KodoJournal(_dir, 100, maxSpillBytes: null);
        journal.Append(At(0), "observation", "one");
        string old = JournalSpillBound.SealedName(SpillPath, At(-1));
        File.WriteAllText(old, "old archive");
        File.SetLastWriteTimeUtc(old, At(0).UtcDateTime.AddDays(-40));

        bool sealedNow = journal.SealSpill(0, At(10), TimeSpan.FromDays(30));

        Assert.True(sealedNow);
        Assert.False(File.Exists(old));
        Assert.Single(SealedFiles());
    }

    [Fact]
    public void Sealing_waits_while_records_are_unsent_and_a_memory_only_journal_has_nothing_to_seal()
    {
        using var journal = new KodoJournal(_dir, 100);
        journal.Append(At(0), "observation", "one");
        journal.Append(At(1), "observation", "two");

        Assert.False(journal.SealSpill(0, At(10), TimeSpan.FromDays(30)));
        Assert.Empty(SealedFiles());
        using var memoryOnly = new KodoJournal(null, 100);
        Assert.True(memoryOnly.SealSpill(-1, At(10), TimeSpan.FromDays(30)));
    }

    private string[] SealedFiles() =>
        Directory.Exists(_dir) ? Directory.GetFiles(_dir, "kodo-journal.spill.sealed-*") : [];
}
