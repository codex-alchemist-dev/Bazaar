using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync;

namespace Bazaar.Server.OfflineSync.Tests;

internal sealed class FakeRule(OfflineDataClass dataClass, TimeSpan? delay = null) : IOfflineMergeRule
{
    private readonly Lock _gate = new();
    private readonly List<OfflineWrite> _applied = [];

    public OfflineDataClass DataClass { get; } = dataClass;

    public IReadOnlyList<OfflineWrite> Applied
    {
        get
        {
            lock (_gate)
            {
                return [.. _applied];
            }
        }
    }

    public async ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        if (delay is { } d)
        {
            await Task.Delay(d, cancellationToken);
        }

        lock (_gate)
        {
            _applied.Add(write);
        }

        return new OfflineWriteOutcome(write.WriteId, OfflineOutcomeKind.Applied, ServerVersion: write.DeviceSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class FixedClock(long now) : IOfflineClock
{
    public long NowMillis { get; } = now;
}

internal sealed class CountingAudit : IOfflineMergeAudit
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public ValueTask RecordAsync(OfflineWrite write, OfflineWriteOutcome outcome, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        return ValueTask.CompletedTask;
    }
}

internal static class Make
{
    public const long Now = 1_000_000_000_000;

    public static OfflineWrite Write(Guid device, long seq, OfflineDataClass cls = OfflineDataClass.Preference, long? wall = null) =>
        new(Guid.NewGuid(), device, seq, cls, "k", null, ReadOnlyMemory<byte>.Empty, new Hlc(wall ?? Now, 0, device));

    public static OfflineWriteBatch Batch(Guid device, IEnumerable<OfflineWrite> writes) => new(device, [.. writes]);

    public static OfflineSyncProcessor Processor(IOfflineSyncStore store, IEnumerable<IOfflineMergeRule> rules, IOfflineMergeAudit? audit = null, OfflineSyncOptions? options = null) =>
        new(rules, store, audit ?? new NullOfflineMergeAudit(), new FixedClock(Now), options ?? new OfflineSyncOptions());
}
