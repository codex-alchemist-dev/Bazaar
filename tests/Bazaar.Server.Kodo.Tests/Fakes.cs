using Bazaar.Contracts.Kodo;
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

    public static KodoMonitor Monitor(FakeReadings readings, FakeTimeProvider time, IKodoStreamPublisher? publisher = null, KodoServerOptions? options = null, IKodoJournalStore? store = null) =>
        new(options ?? new KodoServerOptions(), readings, publisher ?? new NullKodoStreamPublisher(), time, NullLogger<KodoMonitor>.Instance, store);

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

    public ValueTask<IReadOnlyList<KodoStateChange>> RecentStateChangesAsync(int limit, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<KodoStateChange>>([]);

    private void CheckUp()
    {
        if (Down)
        {
            throw new IOException("database unreachable");
        }
    }
}
