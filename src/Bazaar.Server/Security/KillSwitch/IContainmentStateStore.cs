namespace Bazaar.Server.Security.KillSwitch;

/// <summary>One contained agent, as the containment store keeps it.</summary>
/// <param name="AgentId">The contained agent.</param>
/// <param name="ContainedAt">When the agent was contained, from the adapter's <see cref="TimeProvider"/>.</param>
/// <param name="ActorId">The actor recorded for the containment.</param>
public sealed record ContainedAgent(string AgentId, DateTimeOffset ContainedAt, string ActorId);

/// <summary>
/// Durable set of contained agents (migration agent_safety 0003). <see cref="AgentContainmentAdapter"/> adds an agent before it
/// pauses and revokes it, removes it only after the release has finished, and loads the set when it starts, so a restarted
/// host still knows which agents to release.
/// </summary>
/// <remarks>
/// <see cref="AddAsync"/> keeps the first record for an agent that is already contained. A write that throws must leave the
/// stored set as it was.
/// </remarks>
public interface IContainmentStateStore
{
    /// <summary>Reads every contained agent.</summary>
    Task<IReadOnlyList<ContainedAgent>> LoadAsync(CancellationToken ct = default);

    /// <summary>Stores a contained agent. Keeps the existing record when the agent is already stored.</summary>
    Task AddAsync(ContainedAgent agent, CancellationToken ct = default);

    /// <summary>Removes a contained agent. Does nothing when it is not stored.</summary>
    Task RemoveAsync(string agentId, CancellationToken ct = default);
}

/// <summary>
/// One spelling per agent. The same agent can arrive as <c>{ABC...}</c>, an upper-case GUID, a GUID without hyphens or with padding;
/// stores and counters compare ids as plain strings, so each spelling would otherwise be a different agent, and a stop, a counter or a
/// revocation written under one spelling would miss the others. A GUID is written as lower-case <c>D</c> (what the Identity lease store
/// reads back); any other id is only trimmed, since it has no other form to fold to.
/// </summary>
public static class AgentIds
{
    /// <summary>The canonical spelling of <paramref name="agentId"/>.</summary>
    public static string Canonical(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        return Guid.TryParse(agentId, out var guid) ? guid.ToString("D") : agentId.Trim();
    }
}

/// <summary>
/// Per-agent mutual exclusion between a containment release and the periodic re-containment sweep. A release resumes the VMs before
/// it removes the agent from the <see cref="IContainmentStateStore"/>; without this guard a sweep running in between would see a
/// still-stored agent and pause the VMs the release has just resumed. The release waits for the guard; the sweep never waits, it skips
/// an agent whose guard is held (a release in flight, or an earlier sweep still pausing it).
/// </summary>
public sealed class ContainmentReleaseGuard
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>Waits for the agent's guard and holds it until the returned lease is disposed.</summary>
    public async Task<IDisposable> AcquireAsync(string agentId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(AgentIds.Canonical(agentId), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(gate);
    }

    /// <summary>Takes the agent's guard when it is free. Returns <c>null</c> when a release or another sweep holds it.</summary>
    public IDisposable? TryAcquire(string agentId)
    {
        var gate = _locks.GetOrAdd(AgentIds.Canonical(agentId), static _ => new SemaphoreSlim(1, 1));
        return gate.Wait(0) ? new Lease(gate) : null;
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
