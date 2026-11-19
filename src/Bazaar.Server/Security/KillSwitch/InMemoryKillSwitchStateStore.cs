namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Kill switch state held in process memory. The default when no durable <see cref="IKillSwitchStateStore"/> is registered,
/// and the store used in tests. Its contents are lost on restart.
/// </summary>
/// <remarks>Thread-safe: every call takes one lock. Platform-neutral: no OS-specific APIs.</remarks>
public sealed class InMemoryKillSwitchStateStore : IKillSwitchStateStore
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, KillSwitchStop> _agentStops = new(StringComparer.Ordinal);
    private KillSwitchStop? _global;

    /// <inheritdoc />
    public Task<KillSwitchStatus> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(new KillSwitchStatus(_global, _agentStops.Values.ToArray()));
        }
    }

    /// <inheritdoc />
    public Task SetGlobalAsync(KillSwitchStop engaged, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engaged);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _global = engaged;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearGlobalAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _global = null;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetAgentStopAsync(KillSwitchStop engaged, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engaged);
        ArgumentException.ThrowIfNullOrWhiteSpace(engaged.AgentId);
        ct.ThrowIfCancellationRequested();
        var canonical = AgentIds.Canonical(engaged.AgentId);
        lock (_gate)
        {
            _agentStops[canonical] = engaged with { AgentId = canonical };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAgentStopAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _agentStops.Remove(AgentIds.Canonical(agentId));
        }
        return Task.CompletedTask;
    }
}
