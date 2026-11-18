using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>State the GC message rule needs: write-id claims and the per-GC append log.</summary>
public interface IGcAppendState
{
    /// <summary>Atomically claims a write id. Returns false when the id was already claimed.</summary>
    ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken);

    /// <summary>Appends a message to a GC and returns its server sequence (1, 2, ... per GC, gapless).</summary>
    ValueTask<long> AppendAsync(string gcId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

/// <summary>Merge rule for new GC messages (doc 52 section 5, item 1). Appends in arrival order.</summary>
public sealed class GcMessageRule : IOfflineMergeRule
{
    private readonly IGcAppendState _state;

    /// <summary>Creates the rule over the given append state.</summary>
    /// <param name="state">The GC append state.</param>
    public GcMessageRule(IGcAppendState state)
    {
        _state = state;
    }

    /// <inheritdoc />
    public OfflineDataClass DataClass => OfflineDataClass.GcMessage;

    /// <inheritdoc />
    public async ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        if (RulePayloadCodec.ReadGcMessage(write.Payload) is null)
        {
            return RuleOutcomes.Rejected(write.WriteId, "Payload is not a GC message.");
        }

        if (!await _state.TryClaimWriteAsync(write.WriteId, cancellationToken))
        {
            return RuleOutcomes.Duplicate(write.WriteId);
        }

        var sequence = await _state.AppendAsync(write.Target, write.Payload, cancellationToken);
        return RuleOutcomes.Applied(write.WriteId, RuleVersions.Format(sequence));
    }
}

/// <summary>In-memory <see cref="IGcAppendState"/> for tests and single-process use. Thread safe.</summary>
public sealed class InMemoryGcAppendState : IGcAppendState
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _claimed = [];
    private readonly Dictionary<string, List<ReadOnlyMemory<byte>>> _gcs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_claimed.Add(writeId));
        }
    }

    /// <inheritdoc />
    public ValueTask<long> AppendAsync(string gcId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_gcs.TryGetValue(gcId, out var log))
            {
                log = [];
                _gcs[gcId] = log;
            }

            log.Add(payload.ToArray());
            return ValueTask.FromResult((long)log.Count);
        }
    }

    /// <summary>The payloads appended to a GC, in sequence order.</summary>
    /// <param name="gcId">The GC id.</param>
    /// <returns>A snapshot of the log.</returns>
    public IReadOnlyList<ReadOnlyMemory<byte>> GetLog(string gcId)
    {
        lock (_gate)
        {
            return _gcs.TryGetValue(gcId, out var log) ? [.. log] : [];
        }
    }
}
