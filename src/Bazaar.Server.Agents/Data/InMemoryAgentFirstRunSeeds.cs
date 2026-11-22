namespace Bazaar.Server.Agents.Data;

/// <summary>
/// In-memory <see cref="IAgentFirstRunSeeds"/> with the same rules as <see cref="PgAgentFirstRunSeeds"/>. Used by tests and by
/// hosts that run without a database. Turns are not modelled, so <see cref="MarkTurnStarted"/> stands in for a turn start.
/// </summary>
public sealed class InMemoryAgentFirstRunSeeds : IAgentFirstRunSeeds
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Row> _agents = [];
    private readonly HashSet<Guid> _started = [];

    /// <inheritdoc />
    public Task<Guid> CreateHeldAsync(string name, string marker, Guid? soulVersionId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(marker);
        lock (_gate)
        {
            if (_agents.Values.Any(row => row.Marker == marker))
                throw new InvalidOperationException("An agent with this first-run marker already exists. Use FindByMarkerAsync.");
            var id = Guid.CreateVersion7();
            _agents[id] = new Row(id, name, soulVersionId, AgentState.Created, Held: true, marker);
            return Task.FromResult(id);
        }
    }

    /// <inheritdoc />
    public Task<bool> ReleaseHoldAsync(Guid agentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(agentId, out var row) || !row.Held)
                return Task.FromResult(false);
            _agents[agentId] = row with { Held = false };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryStartAutomaticOnboardingAsync(Guid agentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(agentId, out var row) || row.State != AgentState.Created || row.Held)
                return Task.FromResult(false);
            _agents[agentId] = row with { State = AgentState.Onboarding };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<Guid?> FindByMarkerAsync(string marker, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var match = _agents.Values.FirstOrDefault(row => row.Marker == marker);
            return Task.FromResult<Guid?>(match?.AgentId);
        }
    }

    /// <inheritdoc />
    public Task<bool> RemoveUnstartedAsync(Guid agentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_agents.ContainsKey(agentId) || _started.Contains(agentId))
                return Task.FromResult(false);
            _agents.Remove(agentId);
            return Task.FromResult(true);
        }
    }

    /// <summary>Test helper: records that a turn started for the agent, so <see cref="RemoveUnstartedAsync"/> refuses it.</summary>
    public void MarkTurnStarted(Guid agentId)
    {
        lock (_gate)
            _started.Add(agentId);
    }

    /// <summary>Test helper: the agent's state, or null when it does not exist.</summary>
    public AgentState? StateOf(Guid agentId)
    {
        lock (_gate)
            return _agents.TryGetValue(agentId, out var row) ? row.State : null;
    }

    /// <summary>Test helper: whether the agent exists and is still held.</summary>
    public bool IsHeld(Guid agentId)
    {
        lock (_gate)
            return _agents.TryGetValue(agentId, out var row) && row.Held;
    }

    private sealed record Row(Guid AgentId, string Name, Guid? SoulVersionId, AgentState State, bool Held, string Marker);
}
