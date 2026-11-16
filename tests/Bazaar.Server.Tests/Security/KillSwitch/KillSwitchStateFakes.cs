using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.Tests.Security.KillSwitch;

/// <summary>
/// Kill switch store that delegates to an in-memory store and can be told to fail writes, fail the next load, or hold the load
/// until the test releases it. Used to check that a failed write leaves the stop unchanged and that the engine fails closed
/// before its state is loaded.
/// </summary>
internal sealed class ControllableKillSwitchStateStore(IKillSwitchStateStore inner) : IKillSwitchStateStore
{
    /// <summary>When set, every write throws and nothing reaches the inner store.</summary>
    public bool FailWrites { get; set; }

    /// <summary>When set, the next <see cref="LoadAsync"/> throws once.</summary>
    public bool FailNextLoad { get; set; }

    /// <summary>When set, <see cref="LoadAsync"/> waits for this task before it reads.</summary>
    public Task? LoadGate { get; set; }

    public async Task<KillSwitchStatus> LoadAsync(CancellationToken ct = default)
    {
        if (LoadGate is { } gate)
        {
            await gate.ConfigureAwait(false);
        }
        if (FailNextLoad)
        {
            FailNextLoad = false;
            throw new InvalidOperationException("load failed");
        }
        return await inner.LoadAsync(ct).ConfigureAwait(false);
    }

    public Task SetGlobalAsync(KillSwitchStop engaged, CancellationToken ct = default) =>
        FailWrites ? Task.FromException(new InvalidOperationException("write failed")) : inner.SetGlobalAsync(engaged, ct);

    public Task ClearGlobalAsync(CancellationToken ct = default) =>
        FailWrites ? Task.FromException(new InvalidOperationException("write failed")) : inner.ClearGlobalAsync(ct);

    public Task SetAgentStopAsync(KillSwitchStop engaged, CancellationToken ct = default) =>
        FailWrites ? Task.FromException(new InvalidOperationException("write failed")) : inner.SetAgentStopAsync(engaged, ct);

    public Task ClearAgentStopAsync(string agentId, CancellationToken ct = default) =>
        FailWrites ? Task.FromException(new InvalidOperationException("write failed")) : inner.ClearAgentStopAsync(agentId, ct);
}

/// <summary>Containment store that delegates to an in-memory store and can be told to fail writes.</summary>
internal sealed class ControllableContainmentStateStore(IContainmentStateStore inner) : IContainmentStateStore
{
    /// <summary>When set, every write throws and nothing reaches the inner store.</summary>
    public bool FailWrites { get; set; }

    public Task<IReadOnlyList<ContainedAgent>> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);

    public Task AddAsync(ContainedAgent agent, CancellationToken ct = default) =>
        FailWrites ? Task.FromException(new InvalidOperationException("write failed")) : inner.AddAsync(agent, ct);

    public Task RemoveAsync(string agentId, CancellationToken ct = default) =>
        FailWrites ? Task.FromException(new InvalidOperationException("write failed")) : inner.RemoveAsync(agentId, ct);
}
