using System.Collections.Concurrent;
using Bazaar.Server.WorkLife.Scheduler.Abstractions;

namespace Bazaar.Server.WorkLife.Scheduler;

/// <summary>
/// An in-memory agent schedule source (doc 09 WP3 gives the durable one). Callers add and remove agents; the scheduler reads the
/// current set each tick.
/// </summary>
public sealed class InMemoryAgentScheduleSource : IAgentScheduleSource
{
    private readonly ConcurrentDictionary<Guid, AgentShiftEntry> _entries = new();

    /// <summary>Adds or replaces an agent's entry.</summary>
    public void Set(AgentShiftEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[entry.AgentId] = entry;
    }

    /// <summary>Removes an agent. Returns false when it was not present.</summary>
    public bool Remove(Guid agentId) => _entries.TryRemove(agentId, out _);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentShiftEntry>> ListAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AgentShiftEntry>>([.. _entries.Values]);
}

/// <summary>An in-memory last-known-state store; it forgets on restart (the Postgres one is doc 09 WP3).</summary>
public sealed class InMemoryAvailabilityStateStore : IAvailabilityStateStore
{
    private readonly ConcurrentDictionary<Guid, AvailabilityMachineState> _states = new();

    /// <inheritdoc />
    public ValueTask<AvailabilityMachineState?> LoadAsync(Guid agentId, CancellationToken ct) =>
        ValueTask.FromResult<AvailabilityMachineState?>(_states.TryGetValue(agentId, out var s) ? s : null);

    /// <inheritdoc />
    public ValueTask SaveAsync(Guid agentId, AvailabilityMachineState state, CancellationToken ct)
    {
        _states[agentId] = state;
        return ValueTask.CompletedTask;
    }
}

/// <summary>The default drain gate: the drain checklist (doc 09 WP5) is not built, so every drain is complete at once.</summary>
public sealed class ImmediateDrainGate : IDrainGate
{
    /// <inheritdoc />
    public ValueTask<bool> IsDrainCompleteAsync(Guid agentId, CancellationToken ct) => ValueTask.FromResult(true);
}
