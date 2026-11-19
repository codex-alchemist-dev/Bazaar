using Bazaar.Server.Agents.Data;
using Npgsql;

namespace Bazaar.Server.Agents.Scheduling;

/// <summary>
/// Reads the agents that have due work and are allowed to wake. A seam over <see cref="InboxRepository.PeekDueAgentsAsync"/>
/// so <see cref="InboxAgentScheduler"/> can be tested without PostgreSQL.
/// </summary>
public interface IDueAgentReader
{
    /// <summary>Up to <paramref name="limit"/> agents, best due priority first. Reads only; nothing is claimed.</summary>
    Task<IReadOnlyList<DueAgent>> PeekDueAgentsAsync(int limit, CancellationToken ct);
}

/// <summary>The production <see cref="IDueAgentReader"/>: a thin wrapper over <see cref="InboxRepository"/>.</summary>
public sealed class InboxDueAgentReader(NpgsqlDataSource dataSource) : IDueAgentReader
{
    private readonly InboxRepository _inbox = new(dataSource);

    public Task<IReadOnlyList<DueAgent>> PeekDueAgentsAsync(int limit, CancellationToken ct) =>
        _inbox.PeekDueAgentsAsync(limit, ct);
}
