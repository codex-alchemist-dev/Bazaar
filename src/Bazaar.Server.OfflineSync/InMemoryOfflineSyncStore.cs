using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync;

/// <summary>A thread-safe in-memory <see cref="IOfflineSyncStore"/> for tests and development. State is lost on restart.</summary>
public sealed class InMemoryOfflineSyncStore : IOfflineSyncStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, long> _acked = [];
    private readonly Dictionary<(Guid DeviceId, Guid WriteId), OfflineWriteOutcome> _outcomes = [];

    /// <inheritdoc />
    public ValueTask<long> GetAckedSequenceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_acked.GetValueOrDefault(deviceId));
        }
    }

    /// <inheritdoc />
    public ValueTask<OfflineWriteOutcome?> FindOutcomeAsync(Guid deviceId, Guid writeId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_outcomes.GetValueOrDefault((deviceId, writeId)));
        }
    }

    /// <inheritdoc />
    public ValueTask RecordAsync(Guid deviceId, long sequence, OfflineWriteOutcome outcome, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _outcomes[(deviceId, outcome.WriteId)] = outcome;
            if (sequence > _acked.GetValueOrDefault(deviceId))
            {
                _acked[deviceId] = sequence;
            }
        }

        return ValueTask.CompletedTask;
    }
}
