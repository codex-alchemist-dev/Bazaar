namespace Bazaar.Secrets.Fakes;

/// <summary>In-memory <see cref="IAgentRegistry"/> for tests and headless dev runs.</summary>
public sealed class InMemoryAgentRegistry(TimeProvider time) : IAgentRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AgentRecord> _agents = new(StringComparer.Ordinal);

    public Task<AgentRecord> RegisterAsync(AgentId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_agents.TryGetValue(id.Value, out var existing))
            {
                existing = new AgentRecord(id, AgentState.Active, time.GetUtcNow());
                _agents[id.Value] = existing;
            }

            return Task.FromResult(existing);
        }
    }

    public Task<AgentRecord?> GetAsync(AgentId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_agents.GetValueOrDefault(id.Value));
        }
    }

    public Task<AgentRecord> TransitionAsync(AgentId id, AgentState target, OwnerRelease? release, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_agents.TryGetValue(id.Value, out var existing))
            {
                throw new InvalidOperationException($"Agent '{id.Value}' is not registered.");
            }

            if (existing.State == target)
            {
                return Task.FromResult(existing);
            }

            var denial = AgentTransitionRules.Denial(existing.State, target, release);
            if (denial is not null)
            {
                throw new AgentTransitionException(denial);
            }

            var updated = existing with { State = target, UpdatedAt = time.GetUtcNow() };
            _agents[id.Value] = updated;
            return Task.FromResult(updated);
        }
    }
}
