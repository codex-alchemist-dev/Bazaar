using Bazaar.Server.Agents.Data;
using Npgsql;

namespace Bazaar.Server.Agents.Turns.Start;

/// <summary>
/// Reads the due pending inbox items of one agent without claiming them. A seam over <see cref="InboxRepository.PeekDueAsync"/>
/// so <see cref="TurnStarter"/> can be tested without PostgreSQL.
/// </summary>
public interface IDueInboxReader
{
    Task<IReadOnlyList<InboxRow>> PeekDueAsync(Guid agentId, int limit, CancellationToken ct);
}

/// <summary>The production <see cref="IDueInboxReader"/>: a thin wrapper over <see cref="InboxRepository"/>.</summary>
public sealed class InboxDueReader(NpgsqlDataSource dataSource) : IDueInboxReader
{
    private readonly InboxRepository _inbox = new(dataSource);

    public Task<IReadOnlyList<InboxRow>> PeekDueAsync(Guid agentId, int limit, CancellationToken ct) =>
        _inbox.PeekDueAsync(agentId, limit, ct);
}
