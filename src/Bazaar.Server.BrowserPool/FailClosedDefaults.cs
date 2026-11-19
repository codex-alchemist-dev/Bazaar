using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool;

/// <summary>The default gate: blocks every agent until the host registers the real kill-switch gate (doc 15). Fails closed.</summary>
public sealed class UnconfiguredPoolGate : IPoolGate
{
    /// <inheritdoc />
    public bool IsBlocked(string agentId) => true;
}

/// <summary>The default egress policy: no agent has a proxy, so no browser starts until the host registers the real one. Fails closed.</summary>
public sealed class UnconfiguredPoolEgressPolicy : IPoolEgressPolicy
{
    /// <inheritdoc />
    public Task<PoolEgressEndpoint?> ResolveAsync(string agentId, CancellationToken ct) => Task.FromResult<PoolEgressEndpoint?>(null);
}

/// <summary>The default host: every call fails, so nothing runs until the host registers a real pool VM adapter. Fails closed.</summary>
public sealed class UnconfiguredPoolVmHost : IPoolVmHost
{
    private const string _message = "No pool VM host is registered.";

    /// <inheritdoc />
    public Task RunAsync(PoolCommand command, CancellationToken ct) => throw new InvalidOperationException(_message);

    /// <inheritdoc />
    public Task ApplyFirewallAsync(IReadOnlyList<PoolFirewallRule> rules, CancellationToken ct) => throw new InvalidOperationException(_message);

    /// <inheritdoc />
    public Task<PoolProcess> StartAsync(PoolLaunchSpec spec, CancellationToken ct) => throw new InvalidOperationException(_message);

    /// <inheritdoc />
    public Task StopAsync(PoolProcess process, CancellationToken ct) => throw new InvalidOperationException(_message);
}

/// <summary>The default audit sink: writes nothing.</summary>
public sealed class NoOpPoolAuditSink : IPoolAuditSink
{
    /// <inheritdoc />
    public void Opened(PoolSession session)
    {
    }

    /// <inheritdoc />
    public void Closed(PoolSession session, PoolCloseReason reason)
    {
    }

    /// <inheritdoc />
    public void Refused(string agentId, PoolFailure failure, string detail)
    {
    }

    /// <inheritdoc />
    public void SlotQuarantined(int slot, string sessionId, string agentId, string detail)
    {
    }
}
