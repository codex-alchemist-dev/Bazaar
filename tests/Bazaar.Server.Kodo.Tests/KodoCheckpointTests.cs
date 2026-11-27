using System.Text;
using Bazaar.Kodo.Anchoring;
using Bazaar.Kodo.Journal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Kodo.Tests;

public class KodoCheckpointTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new(_t0.AddHours(3));

        public FakeJournalStore Journal { get; } = new();

        public FakeCheckpointStore Store { get; }

        public FakeSink Sink { get; } = new("fake");

        public byte[] Link { get; private set; } = JournalChain.GenesisHash();

        public long Next { get; private set; }

        public Rig() => Store = new FakeCheckpointStore(Journal);

        public void Add(int count)
        {
            for (int i = 0; i < count; i++)
            {
                JournalRecord record = JournalChain.Create(Next, _t0.AddSeconds(Next).AddTicks(7), "observation", Encoding.UTF8.GetBytes($"r{Next}"), Link);
                Journal.Records.Add(record);
                Link = record.Hash;
                Next++;
            }
        }

        public KodoCheckpointScheduler Scheduler(KodoCheckpointOptions? options = null, IEnumerable<IAnchorSink>? sinks = null, IKodoCheckpointSigner? signer = null) =>
            new(Journal, Store, sinks ?? [Sink], options ?? new KodoCheckpointOptions(), Time, NullLogger<KodoCheckpointScheduler>.Instance, signer);
    }

    [Fact]
    public async Task Nothing_is_cut_while_there_is_nothing_new_or_the_cadence_is_not_reached()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler();

        await scheduler.RunPassAsync(ct);
        rig.Add(5);
        await scheduler.RunPassAsync(ct);

        Assert.Empty(rig.Store.Checkpoints);
    }

    [Fact]
    public async Task A_checkpoint_is_cut_when_the_record_count_is_reached()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 10 });
        rig.Add(10);

        await scheduler.RunPassAsync(ct);

        KodoCheckpoint cp = Assert.Single(rig.Store.Checkpoints);
        Assert.Equal((0L, 9L, 10L), (cp.FirstSequence, cp.LastSequence, cp.RecordCount));
        Assert.True(KodoCheckpoints.DigestIsValid(cp));
        Assert.Equal(rig.Journal.Records[9].Hash, cp.HeadHash);
        Assert.Equal(JournalChain.GenesisHash(), cp.PreviousHead);
        Assert.Null(cp.Signature);
    }

    [Fact]
    public async Task A_checkpoint_is_cut_when_the_interval_has_passed_and_there_are_new_records()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler();
        rig.Add(3);
        await scheduler.RunPassAsync(ct);
        Assert.Empty(rig.Store.Checkpoints);

        rig.Time.Advance(TimeSpan.FromMinutes(16));
        await scheduler.RunPassAsync(ct);

        Assert.Equal(3, Assert.Single(rig.Store.Checkpoints).RecordCount);
    }

    [Fact]
    public async Task Windows_are_contiguous_and_each_links_to_the_previous_head()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 4 });
        rig.Add(10);

        await scheduler.RunPassAsync(ct);
        await scheduler.RunPassAsync(ct);
        await scheduler.RunPassAsync(ct);

        Assert.Equal([(0L, 3L), (4L, 7L)], rig.Store.Checkpoints.Select(c => (c.FirstSequence, c.LastSequence)));
        Assert.Equal(rig.Store.Checkpoints[0].HeadHash, rig.Store.Checkpoints[1].PreviousHead);
    }

    [Fact]
    public async Task A_first_window_starts_at_the_oldest_stored_record_when_older_ones_are_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        rig.Add(12);
        rig.Journal.Records.RemoveRange(0, 5);
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 5 });

        await scheduler.RunPassAsync(ct);

        KodoCheckpoint cp = Assert.Single(rig.Store.Checkpoints);
        Assert.Equal((5L, 9L), (cp.FirstSequence, cp.LastSequence));
    }

    [Fact]
    public async Task An_accepted_anchor_is_recorded_and_the_anchor_matches_the_checkpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 5 });
        rig.Add(5);

        await scheduler.RunPassAsync(ct);

        AnchorRecord anchor = Assert.Single(rig.Sink.Published);
        KodoCheckpoint cp = rig.Store.Checkpoints[0];
        Assert.Equal((5L, "fake"), (anchor.TreeSize, anchor.SinkId));
        Assert.Equal(cp.Root, anchor.Root);
        Assert.Equal(cp.HeadHash, anchor.JournalHead);
        Assert.Equal(("fake", 4L), (rig.Store.Anchors[0].Sink, rig.Store.Anchors[0].Sequence));
        Assert.Empty(await rig.Store.ListUnanchoredAsync(10, ct));
    }

    [Fact]
    public async Task A_sink_that_throws_or_declines_leaves_the_checkpoint_unanchored_and_is_retried_after_the_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        rig.Sink.Throw = true;
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 5 });
        rig.Add(5);

        await scheduler.RunPassAsync(ct);
        await scheduler.RunPassAsync(ct);
        Assert.Single(rig.Sink.Published);
        Assert.Empty(rig.Store.Anchors);

        rig.Sink.Throw = false;
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        await scheduler.RunPassAsync(ct);

        Assert.Equal(2, rig.Sink.Published.Count);
        Assert.Single(rig.Store.Anchors);
    }

    [Fact]
    public async Task A_declining_sink_never_counts_as_anchored()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        rig.Sink.Accept = false;
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 5 });
        rig.Add(5);

        await scheduler.RunPassAsync(ct);

        Assert.Empty(rig.Store.Anchors);
        Assert.Single(await rig.Store.ListUnanchoredAsync(10, ct));
    }

    [Fact]
    public async Task With_no_sink_the_checkpoint_is_cut_but_stays_unanchored()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 5 }, sinks: []);
        rig.Add(5);

        await scheduler.RunPassAsync(ct);

        Assert.Single(rig.Store.Checkpoints);
        Assert.Empty(rig.Store.Anchors);
    }

    [Fact]
    public async Task One_accepting_sink_is_enough_and_a_failing_sink_does_not_block_it()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        FakeSink bad = new("bad") { Throw = true };
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 5 }, sinks: [bad, rig.Sink]);
        rig.Add(5);

        await scheduler.RunPassAsync(ct);

        Assert.Equal(["fake"], rig.Store.Anchors.Select(a => a.Sink));
    }

    [Fact]
    public async Task A_down_database_never_throws_into_the_host_and_the_next_pass_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 5 });
        rig.Add(5);
        rig.Journal.Down = true;
        rig.Store.Down = true;

        await scheduler.RunPassAsync(ct);
        Assert.Empty(rig.Store.Checkpoints);

        rig.Journal.Down = false;
        rig.Store.Down = false;
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        await scheduler.RunPassAsync(ct);

        Assert.Single(rig.Store.Checkpoints);
    }

    [Fact]
    public async Task Cancellation_is_the_only_thing_that_escapes()
    {
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 5 });
        rig.Add(5);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scheduler.RunPassAsync(cts.Token));
    }

    [Fact]
    public async Task A_window_that_does_not_link_to_the_previous_checkpoint_is_not_cut()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 3 });
        rig.Add(3);
        await scheduler.RunPassAsync(ct);
        rig.Add(3);
        JournalRecord forged = JournalChain.Create(3, _t0, "observation", [1], JournalChain.GenesisHash());
        rig.Journal.Records[3] = forged;

        await scheduler.RunPassAsync(ct);

        Assert.Single(rig.Store.Checkpoints);
    }

    [Fact]
    public async Task A_signer_signs_the_digest_and_a_failing_signer_leaves_it_unsigned()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler signed = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 2 }, signer: new FakeSigner());
        rig.Add(2);
        await signed.RunPassAsync(ct);
        KodoCheckpoint first = rig.Store.Checkpoints[0];
        Assert.Equal("key-1", first.SignerId);
        Assert.Equal(first.Digest, first.Signature);
        Assert.True(KodoCheckpoints.DigestIsValid(first));

        KodoCheckpointScheduler failing = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 2 }, signer: new FakeSigner { Throw = true });
        rig.Add(2);
        await failing.RunPassAsync(ct);
        Assert.Null(rig.Store.Checkpoints[1].Signature);
    }

    [Fact]
    public async Task The_proof_uses_the_covering_checkpoint_and_verifies_against_its_root()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 4 });
        rig.Add(8);
        await scheduler.RunPassAsync(ct);
        await scheduler.RunPassAsync(ct);

        KodoCheckpointProof proof = await rig.Store.ProofFromCheckpointAsync(5, ct);

        Assert.Equal(KodoCheckpointProofOutcome.Found, proof.Outcome);
        KodoJournalProofView view = proof.View!;
        Assert.Equal((4L, 4L, 7L), (view.WindowStart ?? -1, view.TreeSize, view.CheckpointSequence ?? -1));
        Assert.Equal(Convert.ToHexStringLower(rig.Store.Checkpoints[1].Root), view.Root);
        MerkleInclusionProof inclusion = new(5 - 4, 4, [.. view.Path.Select(Convert.FromHexString)]);
        Assert.True(MerkleVerifier.VerifyInclusion(rig.Journal.Records[5].Hash, inclusion, rig.Store.Checkpoints[1].Root));
    }

    [Fact]
    public async Task A_record_after_the_last_checkpoint_is_not_checkpointed()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 4 });
        rig.Add(6);
        await scheduler.RunPassAsync(ct);

        Assert.Equal(KodoCheckpointProofOutcome.NotCheckpointed, (await rig.Store.ProofFromCheckpointAsync(5, ct)).Outcome);
    }

    [Fact]
    public async Task A_pruned_window_is_reported_as_pruned_not_as_a_missing_record()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 4 });
        rig.Add(8);
        await scheduler.RunPassAsync(ct);
        await scheduler.RunPassAsync(ct);
        long removed = await rig.Store.PruneAnchoredAsync(_t0.AddDays(1), ct);

        Assert.Equal(7, removed);
        Assert.Equal(KodoCheckpointProofOutcome.Pruned, (await rig.Store.ProofFromCheckpointAsync(2, ct)).Outcome);
        Assert.Single(rig.Journal.Records);
        Assert.Equal(2, rig.Store.Checkpoints.Count);
    }

    [Fact]
    public async Task Records_that_no_longer_match_the_checkpoint_root_are_inconsistent()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 4 });
        rig.Add(4);
        await scheduler.RunPassAsync(ct);
        rig.Journal.Records[1] = JournalChain.Create(1, _t0, "observation", [9], rig.Journal.Records[0].Hash);

        Assert.Equal(KodoCheckpointProofOutcome.Inconsistent, (await rig.Store.ProofFromCheckpointAsync(2, ct)).Outcome);
    }

    [Fact]
    public void A_changed_field_breaks_the_digest()
    {
        Rig rig = new();
        rig.Add(3);
        KodoCheckpoint cp = KodoCheckpoints.Build(rig.Journal.Records, _t0)!;

        Assert.True(KodoCheckpoints.DigestIsValid(cp));
        Assert.False(KodoCheckpoints.DigestIsValid(cp with { LastSequence = cp.LastSequence + 1 }));
        Assert.False(KodoCheckpoints.DigestIsValid(cp with { Root = new byte[32] }));
    }

    [Fact]
    public void A_window_with_a_gap_or_a_bad_link_builds_no_checkpoint()
    {
        Rig rig = new();
        rig.Add(4);
        List<JournalRecord> gap = [rig.Journal.Records[0], rig.Journal.Records[2]];

        Assert.Null(KodoCheckpoints.Build(gap, _t0));
        Assert.Null(KodoCheckpoints.Build([], _t0));
    }

    [Theory]
    [InlineData(99L, null, -1L)]
    [InlineData(99L, 49L, 49L)]
    [InlineData(99L, 99L, 98L)]
    [InlineData(0L, 0L, -1L)]
    public void Pruning_never_reaches_past_the_newest_anchored_checkpoint_or_the_head(long head, long? anchored, long expected) =>
        Assert.Equal(expected, KodoPruneRule.HighestPrunable(head, anchored));

    [Fact]
    public async Task Nothing_is_pruned_until_a_checkpoint_is_anchored()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        rig.Sink.Accept = false;
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 4 });
        rig.Add(8);
        await scheduler.RunPassAsync(ct);

        Assert.Equal(0, await rig.Store.PruneAnchoredAsync(_t0.AddDays(1), ct));
        Assert.Equal(8, rig.Journal.Records.Count);
    }

    [Fact]
    public async Task The_tail_after_the_newest_anchored_checkpoint_survives_pruning()
    {
        var ct = TestContext.Current.CancellationToken;
        Rig rig = new();
        KodoCheckpointScheduler scheduler = rig.Scheduler(new KodoCheckpointOptions { MaxRecords = 4 });
        rig.Add(10);
        await scheduler.RunPassAsync(ct);

        long removed = await rig.Store.PruneAnchoredAsync(_t0.AddDays(1), ct);

        Assert.Equal(4, removed);
        Assert.Equal([4L, 5, 6, 7, 8, 9], rig.Journal.Records.Select(r => r.Sequence));
    }

    [Fact]
    public void Options_are_validated_and_the_scheduler_is_off_unless_configured()
    {
        Assert.False(new KodoServerOptions().CheckpointsEnabled);
        Assert.False(new KodoCheckpointOptions { MaxRecords = 0 }.IsValid);
        Assert.False(new KodoCheckpointOptions { MaxRecords = 100_001 }.IsValid);
        Assert.False(new KodoCheckpointOptions { Interval = TimeSpan.Zero }.IsValid);
        Assert.True(new KodoCheckpointOptions().IsValid);
        Assert.Throws<ArgumentException>(() => new Rig().Scheduler(new KodoCheckpointOptions { MaxRecords = 0 }));
    }

    [Fact]
    public async Task The_file_sink_appends_one_canonical_line_per_anchor()
    {
        var ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(Path.GetTempPath(), "kodo-anchor-" + Guid.NewGuid().ToString("N"));
        try
        {
            using FileAnchorSink sink = new(Path.Combine(dir, "anchors.jsonl"));
            Rig rig = new();
            rig.Add(3);
            KodoCheckpoint cp = KodoCheckpoints.Build(rig.Journal.Records, _t0)!;
            AnchorRecord anchor = KodoCheckpoints.ToAnchor(cp, sink.Id, _t0);

            Assert.True(await sink.PublishAsync(anchor, ct));
            Assert.True(await sink.PublishAsync(anchor, ct));

            string[] lines = await File.ReadAllLinesAsync(Path.Combine(dir, "anchors.jsonl"), ct);
            Assert.Equal(2, lines.Length);
            Assert.Equal(anchor.Root, AnchorCodec.TryDeserialize(Encoding.UTF8.GetBytes(lines[0]))!.Root);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task The_no_op_sink_never_accepts()
    {
        NoOpAnchorSink sink = new();
        Rig rig = new();
        rig.Add(1);
        AnchorRecord anchor = KodoCheckpoints.ToAnchor(KodoCheckpoints.Build(rig.Journal.Records, _t0)!, sink.Id, _t0);

        Assert.False(await sink.PublishAsync(anchor, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Configuration_turns_checkpoints_on_and_bad_values_fall_back_with_an_error()
    {
        IConfigurationSection Section(params (string, string)[] values) => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => "Bazaar:Kodo:" + v.Item1, v => (string?)v.Item2)).Build().GetSection("Bazaar:Kodo");

        KodoServerOptions off = KodoServiceCollectionExtensions.ReadOptions(Section());
        KodoServerOptions on = KodoServiceCollectionExtensions.ReadOptions(Section(("CheckpointsEnabled", "true"), ("CheckpointMaxRecords", "50"), ("CheckpointIntervalSeconds", "30"), ("AnchorRetrySeconds", "5"), ("AnchorFilePath", "a.jsonl")));
        KodoServerOptions bad = KodoServiceCollectionExtensions.ReadOptions(Section(("CheckpointsEnabled", "true"), ("CheckpointMaxRecords", "999999999")));

        Assert.False(off.CheckpointsEnabled);
        Assert.Equal((true, 50, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), "a.jsonl"), (on.CheckpointsEnabled, on.CheckpointMaxRecords, on.CheckpointInterval, on.AnchorRetryInterval, on.AnchorFilePath));
        Assert.Equal(1000, bad.CheckpointMaxRecords);
        Assert.Contains(bad.SettingErrors, e => e.StartsWith("Checkpoint settings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_monitor_answers_proofs_from_the_checkpoint_and_says_pruned_when_the_window_is_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = Make.Clock();
        var readings = new FakeReadings();
        FakeJournalStore journal = new();
        FakeCheckpointStore checkpoints = new(journal);
        using var monitor = Make.Monitor(readings, time, store: journal, checkpoints: checkpoints);
        for (int i = 0; i < 4; i++)
        {
            readings.DiskUsedPercent = i % 2 == 0 ? 97 : 40;
            time.Advance(TimeSpan.FromMinutes(1));
            await monitor.RunCycleAsync(ct);
        }

        Assert.True(journal.Records.Count >= 4);
        Assert.Equal(KodoProofOutcome.Found, (await monitor.LookupJournalProofAsync(1, ct)).Outcome);
        Assert.Null((await monitor.LookupJournalProofAsync(1, ct)).View!.CheckpointSequence);

        KodoCheckpointScheduler scheduler = new(journal, checkpoints, [new FakeSink("fake")], new KodoCheckpointOptions { MaxRecords = 3 }, time, NullLogger<KodoCheckpointScheduler>.Instance);
        await scheduler.RunPassAsync(ct);

        KodoProofLookup found = await monitor.LookupJournalProofAsync(1, ct);
        Assert.Equal(KodoProofOutcome.Found, found.Outcome);
        Assert.Equal(2, found.View!.CheckpointSequence);
        IReadOnlyList<KodoCheckpointView> anchors = await monitor.JournalAnchorsAsync(10, ct);
        KodoCheckpointView view = Assert.Single(anchors);
        Assert.True(view.DigestValid);
        Assert.Equal("fake", Assert.Single(view.Anchors).SinkId);

        journal.Records.RemoveAll(r => r.Sequence == 1);
        Assert.Equal(KodoProofOutcome.Pruned, (await monitor.LookupJournalProofAsync(1, ct)).Outcome);

        checkpoints.Down = true;
        KodoProofLookup fallback = await monitor.LookupJournalProofAsync(1, ct);
        Assert.Equal(KodoProofOutcome.Found, fallback.Outcome);
        Assert.Null(fallback.View!.CheckpointSequence);
        Assert.Empty(await monitor.JournalAnchorsAsync(10, ct));
    }

    [Fact]
    public async Task Retention_through_the_sync_keeps_the_tail_after_the_newest_anchored_checkpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = Make.Clock();
        var readings = new FakeReadings();
        FakeJournalStore journal = new();
        FakeCheckpointStore checkpoints = new(journal);
        KodoServerOptions options = new() { JournalRetentionDays = 1 };
        using var monitor = Make.Monitor(readings, time, options: options, store: journal, checkpoints: checkpoints);
        for (int i = 0; i < 4; i++)
        {
            readings.DiskUsedPercent = i % 2 == 0 ? 97 : 40;
            time.Advance(TimeSpan.FromMinutes(1));
            await monitor.RunCycleAsync(ct);
        }

        KodoCheckpointScheduler scheduler = new(journal, checkpoints, [new FakeSink("fake")], new KodoCheckpointOptions { MaxRecords = 3 }, time, NullLogger<KodoCheckpointScheduler>.Instance);
        await scheduler.RunPassAsync(ct);
        long anchored = checkpoints.Checkpoints.Single().LastSequence;

        time.Advance(TimeSpan.FromDays(3));
        await monitor.RunCycleAsync(ct);

        Assert.DoesNotContain(journal.Records, r => r.Sequence <= anchored);
        Assert.Contains(journal.Records, r => r.Sequence == anchored + 1);
    }

    private sealed class FakeSigner : IKodoCheckpointSigner
    {
        public string Id => "key-1";

        public bool Throw { get; set; }

        public ValueTask<byte[]> SignAsync(byte[] digest, CancellationToken cancellationToken) =>
            Throw ? throw new IOException("no key") : ValueTask.FromResult(digest);
    }
}
