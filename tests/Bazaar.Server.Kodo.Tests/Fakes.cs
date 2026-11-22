using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Anchoring;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Probes.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Kodo.Tests;

internal sealed class FakeReadings : IHostReadings
{
    public int DiskUsedPercent { get; set; } = 40;

    public int MemoryUsedPercent { get; set; } = 40;

    public bool Throw { get; set; }

    public int DiskReads { get; private set; }

    public int MemoryReads { get; private set; }

    public Task<DiskReading> ReadDiskAsync(string volume, CancellationToken cancellationToken)
    {
        DiskReads++;
        if (Throw)
        {
            throw new IOException(@"C:\secret\path failed");
        }

        return Task.FromResult(new DiskReading(100 - DiskUsedPercent, 100));
    }

    public Task<MemoryReading> ReadMemoryAsync(CancellationToken cancellationToken)
    {
        MemoryReads++;
        return Task.FromResult(new MemoryReading(100 - MemoryUsedPercent, 100));
    }

    public Task<TimeSpan> ReadClockOffsetAsync(CancellationToken cancellationToken) => Task.FromResult(TimeSpan.Zero);
}

internal sealed class RecordingPublisher : IKodoStreamPublisher
{
    public List<KodoStreamEventDto> Events { get; } = [];

    public bool Throw { get; set; }

    public ValueTask PublishAsync(KodoStreamEventDto streamEvent, CancellationToken cancellationToken)
    {
        if (Throw)
        {
            throw new InvalidOperationException("transport down");
        }

        lock (Events)
        {
            Events.Add(streamEvent);
        }

        return ValueTask.CompletedTask;
    }
}

internal static class Make
{
    public static readonly DateTimeOffset Start = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public static KodoMonitor Monitor(FakeReadings readings, FakeTimeProvider time, IKodoStreamPublisher? publisher = null, KodoServerOptions? options = null, IKodoJournalStore? store = null, IKodoCheckpointStore? checkpoints = null) =>
        new(options ?? new KodoServerOptions(), readings, publisher ?? new NullKodoStreamPublisher(), time, NullLogger<KodoMonitor>.Instance, store, checkpoints);

    public static FakeTimeProvider Clock() => new(Start);
}

/// <summary>An in-memory durable store that can be switched off to look like an unreachable database.</summary>
internal sealed class FakeJournalStore : IKodoJournalStore
{
    public List<JournalRecord> Records { get; } = [];

    public bool Down { get; set; }

    public int AppendCalls { get; private set; }

    public KodoStoredSnapshot? Snapshot { get; private set; }

    public KodoJournalProofView? ProofToReturn { get; set; }

    public int ProofCalls { get; private set; }

    public ValueTask<JournalRecord?> GetHeadAsync(CancellationToken cancellationToken)
    {
        CheckUp();
        return ValueTask.FromResult(Records.Count == 0 ? null : Records[^1]);
    }

    public ValueTask<KodoJournalAppendResult> AppendAsync(IReadOnlyList<JournalRecord> records, CancellationToken cancellationToken)
    {
        CheckUp();
        AppendCalls++;
        int stored = 0, skipped = 0;
        foreach (JournalRecord record in records)
        {
            if (Records.Any(r => r.Sequence == record.Sequence))
            {
                skipped++;
                continue;
            }

            if (Records.Count > 0 && !record.PreviousHash.AsSpan().SequenceEqual(Records[^1].Hash))
            {
                throw new KodoJournalConflictException(KodoJournalConflict.Diverged, "does not continue the head");
            }

            Records.Add(record);
            stored++;
        }

        return ValueTask.FromResult(new KodoJournalAppendResult(stored, skipped));
    }

    public ValueTask<IReadOnlyList<JournalRecord>> ReadRangeAsync(long fromSequence, int limit, CancellationToken cancellationToken)
    {
        CheckUp();
        return ValueTask.FromResult<IReadOnlyList<JournalRecord>>([.. Records.Where(r => r.Sequence >= fromSequence).Take(limit)]);
    }

    public ValueTask<KodoJournalProofView?> ProofAsync(long sequence, CancellationToken cancellationToken)
    {
        CheckUp();
        ProofCalls++;
        return ValueTask.FromResult(ProofToReturn);
    }

    public ValueTask<long> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        CheckUp();
        return ValueTask.FromResult(0L);
    }

    public ValueTask SaveSnapshotAsync(long revision, byte[] snapshotJson, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        CheckUp();
        Snapshot = new KodoStoredSnapshot(revision, snapshotJson, savedAt);
        return ValueTask.CompletedTask;
    }

    public ValueTask<KodoStoredSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Snapshot);

    public List<KodoStateChange> StateChanges { get; } = [];

    public int HistoryCalls { get; private set; }

    public ValueTask<IReadOnlyList<KodoStateChange>> RecentStateChangesAsync(int limit, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<KodoStateChange>>([.. StateChanges.OrderByDescending(c => c.Sequence).Take(limit)]);

    public ValueTask<IReadOnlyList<KodoStateChange>> ReadStateChangesAsync(long? beforeSequence, int limit, CancellationToken cancellationToken)
    {
        CheckUp();
        HistoryCalls++;
        return ValueTask.FromResult<IReadOnlyList<KodoStateChange>>(
            [.. StateChanges.Where(c => beforeSequence is null || c.Sequence < beforeSequence).OrderByDescending(c => c.Sequence).Take(limit)]);
    }

    private void CheckUp()
    {
        if (Down)
        {
            throw new IOException("database unreachable");
        }
    }
}

/// <summary>An in-memory checkpoint store over a <see cref="FakeJournalStore"/>.</summary>
internal sealed class FakeCheckpointStore(FakeJournalStore journal) : IKodoCheckpointStore
{
    public List<KodoCheckpoint> Checkpoints { get; } = [];

    public List<(long Sequence, string Sink, DateTimeOffset At)> Anchors { get; } = [];

    public bool Down { get; set; }

    public ValueTask<bool> SaveCheckpointAsync(KodoCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        CheckUp();
        if (Checkpoints.Any(c => c.LastSequence == checkpoint.LastSequence))
        {
            return ValueTask.FromResult(false);
        }

        Checkpoints.Add(checkpoint);
        return ValueTask.FromResult(true);
    }

    public ValueTask<KodoCheckpoint?> GetLatestCheckpointAsync(CancellationToken cancellationToken)
    {
        CheckUp();
        return ValueTask.FromResult(Checkpoints.OrderBy(c => c.LastSequence).LastOrDefault());
    }

    public ValueTask<long?> GetFirstStoredSequenceAsync(CancellationToken cancellationToken)
    {
        CheckUp();
        return ValueTask.FromResult<long?>(journal.Records.Count == 0 ? null : journal.Records.Min(r => r.Sequence));
    }

    public ValueTask<IReadOnlyList<KodoCheckpoint>> ListUnanchoredAsync(int limit, CancellationToken cancellationToken)
    {
        CheckUp();
        return ValueTask.FromResult<IReadOnlyList<KodoCheckpoint>>([.. Checkpoints.Where(c => !Anchors.Any(a => a.Sequence == c.LastSequence)).OrderBy(c => c.LastSequence).Take(limit)]);
    }

    public ValueTask<IReadOnlyList<KodoCheckpointEntry>> ListCheckpointsAsync(int limit, CancellationToken cancellationToken)
    {
        CheckUp();
        return ValueTask.FromResult<IReadOnlyList<KodoCheckpointEntry>>([.. Checkpoints.OrderByDescending(c => c.LastSequence).Take(limit)
            .Select(c => new KodoCheckpointEntry(c, [.. Anchors.Where(a => a.Sequence == c.LastSequence).Select(a => new KodoAnchorMark(a.Sink, a.At))]))]);
    }

    public ValueTask RecordAnchorAsync(long checkpointSequence, string sinkId, DateTimeOffset anchoredAt, CancellationToken cancellationToken)
    {
        CheckUp();
        if (!Anchors.Any(a => a.Sequence == checkpointSequence && a.Sink == sinkId))
        {
            Anchors.Add((checkpointSequence, sinkId, anchoredAt));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<KodoCheckpointProof> ProofFromCheckpointAsync(long sequence, CancellationToken cancellationToken)
    {
        CheckUp();
        KodoCheckpoint? cover = Checkpoints.OrderBy(c => c.LastSequence).FirstOrDefault(c => c.LastSequence >= sequence && c.FirstSequence <= sequence);
        if (cover is null)
        {
            return ValueTask.FromResult(new KodoCheckpointProof(KodoCheckpointProofOutcome.NotCheckpointed));
        }

        List<KeyValuePair<long, byte[]>> hashes = [.. journal.Records.Where(r => r.Sequence >= cover.FirstSequence && r.Sequence <= cover.LastSequence)
            .OrderBy(r => r.Sequence).Select(r => new KeyValuePair<long, byte[]>(r.Sequence, r.Hash))];
        return ValueTask.FromResult(KodoCheckpoints.BuildProof(sequence, cover, hashes));
    }

    public ValueTask<long> PruneAnchoredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        CheckUp();
        long? anchored = Checkpoints.Where(c => Anchors.Any(a => a.Sequence == c.LastSequence)).Select(c => (long?)c.LastSequence).Max();
        long highest = journal.Records.Count == 0 ? -1 : KodoPruneRule.HighestPrunable(journal.Records.Max(r => r.Sequence), anchored);
        int removed = journal.Records.RemoveAll(r => r.Sequence <= highest && r.At < olderThan);
        return ValueTask.FromResult((long)removed);
    }

    private void CheckUp()
    {
        if (Down)
        {
            throw new IOException("database unreachable");
        }
    }
}

internal sealed class FakeSink(string id, Func<AnchorRecord, bool>? accept = null) : IAnchorSink
{
    public string Id { get; } = id;

    public bool Throw { get; set; }

    public bool Accept { get; set; } = true;

    public List<AnchorRecord> Published { get; } = [];

    public ValueTask<bool> PublishAsync(AnchorRecord anchor, CancellationToken cancellationToken)
    {
        Published.Add(anchor);
        if (Throw)
        {
            throw new IOException("sink down");
        }

        return ValueTask.FromResult(Accept && (accept?.Invoke(anchor) ?? true));
    }
}
