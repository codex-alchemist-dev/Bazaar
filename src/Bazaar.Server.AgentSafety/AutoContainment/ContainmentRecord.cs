using Bazaar.Server.AgentSafety.Anomaly;

namespace Bazaar.Server.AgentSafety.AutoContainment;

/// <summary>Where an automatic containment stands.</summary>
public enum ContainmentRecordState
{
    /// <summary>Containment completed and has not been undone. Undo with a signed release is allowed until <see cref="ContainmentRecord.UndoUntil"/>.</summary>
    InForce,

    /// <summary>The owner undid the containment inside the window, with a signed release.</summary>
    Undone,

    /// <summary>The undo window elapsed while the containment was in force. Only the normal owner release path applies.</summary>
    WindowExpired,

    /// <summary>Containment was tried and did not complete. The agent may be partly contained; the owner was told.</summary>
    Failed,

    /// <summary>The agent was released by the normal owner path before the record was undone, so the record is history only.</summary>
    Released,
}

/// <summary>One automatic containment. Holds no alert detail and no message content.</summary>
/// <param name="Id">Record id.</param>
/// <param name="AgentId">The contained agent.</param>
/// <param name="RuleId">The rule whose alert triggered it.</param>
/// <param name="Severity">The alert's severity.</param>
/// <param name="ContainedAt">When containment completed (or, for a failed one, when the last attempt ended).</param>
/// <param name="UndoUntil">End of the undo window.</param>
/// <param name="State">Current state.</param>
public sealed record ContainmentRecord(
    Guid Id,
    string AgentId,
    string RuleId,
    AnomalySeverity Severity,
    DateTimeOffset ContainedAt,
    DateTimeOffset UndoUntil,
    ContainmentRecordState State);

/// <summary>
/// Durable record of automatic containments (agent_safety migration 0005). At most one record per agent is
/// <see cref="ContainmentRecordState.InForce"/> at a time.
/// </summary>
public interface IContainmentRecordStore
{
    /// <summary>Stores a record. Returns false, storing nothing, when it is InForce and the agent already has an InForce record.</summary>
    Task<bool> TryAddAsync(ContainmentRecord record, CancellationToken ct = default);

    /// <summary>The agent's most recent record by <see cref="ContainmentRecord.ContainedAt"/>, or null.</summary>
    Task<ContainmentRecord?> GetLatestAsync(string agentId, CancellationToken ct = default);

    /// <summary>Moves a record to <paramref name="target"/> only while it is in <paramref name="expected"/>. Returns whether it moved.</summary>
    Task<bool> TryTransitionAsync(Guid id, ContainmentRecordState expected, ContainmentRecordState target, CancellationToken ct = default);

    /// <summary>Marks every InForce record whose window ended at or before <paramref name="now"/> as WindowExpired. Returns how many.</summary>
    Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>Record store held in process memory, for tests and single-process use. Thread-safe.</summary>
public sealed class InMemoryContainmentRecordStore : IContainmentRecordStore
{
    private readonly Lock _gate = new();
    private readonly List<ContainmentRecord> _records = [];

    /// <summary>Every record, oldest first.</summary>
    public IReadOnlyList<ContainmentRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> TryAddAsync(ContainmentRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (record.State == ContainmentRecordState.InForce
                && _records.Exists(r => r.AgentId == record.AgentId && r.State == ContainmentRecordState.InForce))
            {
                return Task.FromResult(false);
            }
            _records.Add(record);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<ContainmentRecord?> GetLatestAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ContainmentRecord? latest = null;
            foreach (var r in _records)
            {
                if (r.AgentId == agentId && (latest is null || r.ContainedAt >= latest.ContainedAt))
                {
                    latest = r;
                }
            }
            return Task.FromResult(latest);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryTransitionAsync(Guid id, ContainmentRecordState expected, ContainmentRecordState target, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var i = _records.FindIndex(r => r.Id == id && r.State == expected);
            if (i < 0)
            {
                return Task.FromResult(false);
            }
            _records[i] = _records[i] with { State = target };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var count = 0;
            for (var i = 0; i < _records.Count; i++)
            {
                if (_records[i].State == ContainmentRecordState.InForce && _records[i].UndoUntil <= now)
                {
                    _records[i] = _records[i] with { State = ContainmentRecordState.WindowExpired };
                    count++;
                }
            }
            return Task.FromResult(count);
        }
    }
}
