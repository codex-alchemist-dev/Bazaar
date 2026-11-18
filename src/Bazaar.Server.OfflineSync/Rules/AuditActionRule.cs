using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>
/// The audit chain's append point. Implementations append in arrival order and dedupe on the write id
/// atomically with the append.
/// </summary>
public interface IAuditActionSink
{
    /// <summary>
    /// Appends one client action. Returns its arrival position (1, 2, ...), or null when this write id
    /// was already appended.
    /// </summary>
    ValueTask<long?> TryAppendAsync(Guid writeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

/// <summary>
/// Merge rule for client audit actions (doc 56 section 5, item 7). Actions are appended in arrival
/// order. The audit module hashes them into the chain; the client never sets an order.
/// </summary>
public sealed class AuditActionRule : IOfflineMergeRule
{
    private readonly IAuditActionSink _sink;

    /// <summary>Creates the rule over the given sink.</summary>
    /// <param name="sink">The audit append point.</param>
    public AuditActionRule(IAuditActionSink sink)
    {
        _sink = sink;
    }

    /// <inheritdoc />
    public OfflineDataClass DataClass => OfflineDataClass.AuditAction;

    /// <inheritdoc />
    public async ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        var position = await _sink.TryAppendAsync(write.WriteId, write.Payload, cancellationToken);
        return position is null
            ? RuleOutcomes.Duplicate(write.WriteId)
            : RuleOutcomes.Applied(write.WriteId, RuleVersions.Format(position.Value));
    }
}

/// <summary>In-memory <see cref="IAuditActionSink"/> for tests and single-process use. Thread safe.</summary>
public sealed class InMemoryAuditActionSink : IAuditActionSink
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _appended = [];
    private readonly List<Guid> _order = [];

    /// <inheritdoc />
    public ValueTask<long?> TryAppendAsync(Guid writeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_appended.Add(writeId))
            {
                return ValueTask.FromResult<long?>(null);
            }

            _order.Add(writeId);
            return ValueTask.FromResult<long?>(_order.Count);
        }
    }

    /// <summary>The write ids in the order they were appended.</summary>
    public IReadOnlyList<Guid> WriteIdsInOrder
    {
        get
        {
            lock (_gate)
            {
                return [.. _order];
            }
        }
    }
}
