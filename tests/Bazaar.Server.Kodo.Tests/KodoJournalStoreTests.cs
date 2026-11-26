using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Self;

namespace Bazaar.Server.Kodo.Tests;

/// <summary>The monitor's use of the durable journal store: replay, outage fallback, restart continuity, forks and proofs.</summary>
public class KodoJournalStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KodoServerComponentStatus Spill(KodoMonitor monitor) => new(monitor.Latest!.Components.Single(c => c.Id == SpillAgeProbe.Id));

    private readonly record struct KodoServerComponentStatus(KodoComponentDto Component)
    {
        public string State => Component.State;

        public string? Cause => Component.Cause;
    }

    [Fact]
    public async Task A_pass_replays_the_local_journal_into_the_store_and_saves_the_snapshot()
    {
        var time = Make.Clock();
        var store = new FakeJournalStore();
        using var monitor = Make.Monitor(new FakeReadings(), time, store: store);

        await monitor.RunCycleAsync(Ct);

        Assert.Equal(monitor.RecentJournal(200).Count, store.Records.Count);
        Assert.Null(JournalChain.Verify(store.Records).FirstBrokenIndex);
        Assert.NotNull(store.Snapshot);
        Assert.Equal("green", Spill(monitor).State);
    }

    [Fact]
    public async Task A_database_outage_keeps_the_local_journal_going_and_the_backlog_is_replayed_when_it_returns()
    {
        var time = Make.Clock();
        var readings = new FakeReadings();
        var store = new FakeJournalStore();
        using var monitor = Make.Monitor(readings, time, store: store);
        await monitor.RunCycleAsync(Ct);
        int before = store.Records.Count;

        store.Down = true;
        readings.DiskUsedPercent = 97;
        time.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync(Ct);
        readings.DiskUsedPercent = 40;
        time.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync(Ct);

        Assert.Equal(before, store.Records.Count);
        Assert.True(monitor.RecentJournal(200).Count > before);

        store.Down = false;
        time.Advance(TimeSpan.FromSeconds(10));
        await monitor.RunCycleAsync(Ct);

        Assert.Equal(monitor.RecentJournal(200).Count, store.Records.Count);
        Assert.Null(JournalChain.Verify(store.Records).FirstBrokenIndex);
        Assert.Equal("green", Spill(monitor).State);
    }

    [Fact]
    public async Task A_backlog_older_than_the_warn_age_turns_the_spill_probe_amber()
    {
        var time = Make.Clock();
        var readings = new FakeReadings();
        var store = new FakeJournalStore { Down = true };
        using var monitor = Make.Monitor(readings, time, store: store);

        await monitor.RunCycleAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(61));
        await monitor.RunCycleAsync(Ct);

        Assert.Empty(store.Records);
        Assert.Equal("amber", Spill(monitor).State);
    }

    [Fact]
    public async Task A_restart_with_no_local_chain_continues_the_stored_chain()
    {
        var time = Make.Clock();
        var store = new FakeJournalStore();
        using (var first = Make.Monitor(new FakeReadings(), time, store: store))
        {
            await first.RunCycleAsync(Ct);
        }

        int stored = store.Records.Count;
        JournalRecord head = store.Records[^1];
        using var second = Make.Monitor(new FakeReadings(), time, store: store);
        await second.RunCycleAsync(Ct);

        Assert.True(store.Records.Count > stored);
        Assert.Equal(head.Sequence + 1, store.Records[stored].Sequence);
        Assert.True(head.Hash.AsSpan().SequenceEqual(store.Records[stored].PreviousHash));
        Assert.Null(JournalChain.Verify(store.Records).FirstBrokenIndex);
    }

    [Fact]
    public async Task A_local_chain_that_forks_from_the_stored_one_stops_the_sync_and_turns_the_spill_probe_red()
    {
        var time = Make.Clock();
        var store = new FakeJournalStore { Down = true };
        using var monitor = Make.Monitor(new FakeReadings(), time, store: store);
        await monitor.RunCycleAsync(Ct);

        JournalRecord? previous = null;
        for (int i = 0; i < 50; i++)
        {
            previous = JournalChain.Create(i, Make.Start, "observation", [(byte)i, 7], previous?.Hash ?? JournalChain.GenesisHash());
            store.Records.Add(previous);
        }

        store.Down = false;
        time.Advance(TimeSpan.FromMinutes(2));
        await monitor.RunCycleAsync(Ct);
        int calls = store.AppendCalls;
        time.Advance(TimeSpan.FromMinutes(2));
        await monitor.RunCycleAsync(Ct);

        Assert.Equal(50, store.Records.Count);
        Assert.Equal(calls, store.AppendCalls);
        Assert.Equal("red", Spill(monitor).State);
        Assert.Equal(ProbeCauses.SpillSealed, Spill(monitor).Cause);
    }

    [Fact]
    public async Task Without_a_store_or_with_it_switched_off_nothing_is_sent()
    {
        var store = new FakeJournalStore();
        using var off = Make.Monitor(new FakeReadings(), Make.Clock(), options: new KodoServerOptions { UseJournalStore = false }, store: store);
        using var none = Make.Monitor(new FakeReadings(), Make.Clock());

        await off.RunCycleAsync(Ct);
        await none.RunCycleAsync(Ct);

        Assert.Empty(store.Records);
        Assert.Equal(0, store.AppendCalls);
        Assert.Equal("green", Spill(none).State);
        Assert.Null(await none.JournalProofAsync(99, Ct));
    }

    [Fact]
    public async Task A_proof_older_than_the_memory_window_comes_from_the_store()
    {
        var time = Make.Clock();
        var readings = new FakeReadings();
        var store = new FakeJournalStore();
        var canned = new KodoJournalProofView(0, 9, "aa", "bb", ["cc"]);
        using var monitor = Make.Monitor(readings, time, options: new KodoServerOptions { JournalMemoryRecords = 2 }, store: store);
        for (int i = 0; i < 5; i++)
        {
            readings.DiskUsedPercent = i % 2 == 0 ? 97 : 40;
            time.Advance(TimeSpan.FromMinutes(1));
            await monitor.RunCycleAsync(Ct);
        }

        Assert.Null(monitor.JournalProof(0));
        Assert.Null(await monitor.JournalProofAsync(0, Ct));

        store.ProofToReturn = canned;
        Assert.Equal(canned, await monitor.JournalProofAsync(0, Ct));
        Assert.Null(await monitor.JournalProofAsync(10_000, Ct));

        store.Down = true;
        Assert.Null(await monitor.JournalProofAsync(0, Ct));
    }

    [Fact]
    public async Task With_a_spill_folder_and_no_spill_records_the_stored_chain_is_adopted_and_the_journal_stays_writable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kodo-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var time = Make.Clock();
            var store = new FakeJournalStore();
            using (var first = Make.Monitor(new FakeReadings(), time, store: store))
            {
                await first.RunCycleAsync(Ct);
            }

            int stored = store.Records.Count;
            using var second = Make.Monitor(new FakeReadings(), time, options: new KodoServerOptions { DataDirectory = dir }, store: store);
            await second.RunCycleAsync(Ct);

            Assert.True(store.Records.Count > stored);
            Assert.Equal(stored, store.Records[stored].Sequence);
            Assert.Null(JournalChain.Verify(store.Records).FirstBrokenIndex);
            Assert.Equal("green", second.Latest!.Components.Single(c => c.Id == JournalWritableProbe.Id).State);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
