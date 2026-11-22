namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Contained-agent set held in process memory. The default when no durable <see cref="IContainmentStateStore"/> is registered,
/// and the store used in tests. Its contents are lost on restart.
/// </summary>
/// <remarks>Thread-safe: every call takes one lock. Platform-neutral: no OS-specific APIs.</remarks>
public sealed class InMemoryContainmentStateStore : IContainmentStateStore
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, ContainedAgent> _agents = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContainedAgent>> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ContainedAgent>>(_agents.Values.ToArray());
        }
    }

    /// <inheritdoc />
    public Task AddAsync(ContainedAgent agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.AgentId);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _agents.TryAdd(agent.AgentId, agent);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _agents.Remove(agentId);
        }
        return Task.CompletedTask;
    }
}
