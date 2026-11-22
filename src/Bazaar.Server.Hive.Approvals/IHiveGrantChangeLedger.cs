using System.Collections.Concurrent;
using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive.Approvals;

/// <summary>
/// Remembers which grant change each queued approval stands for. The approval row carries only a key and a summary, so the
/// change itself is kept here until the owner decides. A change that is not in the ledger is never applied.
/// </summary>
public interface IHiveGrantChangeLedger
{
    /// <summary>Records the change an approval stands for.</summary>
    Task AddAsync(Guid ticketId, HiveGrantChange change, CancellationToken ct);

    /// <summary>Every change still waiting for a decision.</summary>
    Task<IReadOnlyList<(Guid TicketId, HiveGrantChange Change)>> SnapshotAsync(CancellationToken ct);

    /// <summary>Forgets a ticket once it is decided or applied.</summary>
    Task RemoveAsync(Guid ticketId, CancellationToken ct);
}

/// <summary>
/// In-process ledger, used when the host has no database. A restart forgets pending changes: their cards expire unused and the
/// owner asks again, so a lost entry can only fail to apply, never apply something wrong.
/// </summary>
public sealed class InMemoryHiveGrantChangeLedger : IHiveGrantChangeLedger
{
    private readonly ConcurrentDictionary<Guid, HiveGrantChange> _changes = new();

    /// <inheritdoc />
    public Task AddAsync(Guid ticketId, HiveGrantChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        _changes[ticketId] = change;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<(Guid TicketId, HiveGrantChange Change)>> SnapshotAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(Guid TicketId, HiveGrantChange Change)>>([.. _changes.Select(p => (p.Key, p.Value))]);

    /// <inheritdoc />
    public Task RemoveAsync(Guid ticketId, CancellationToken ct)
    {
        _changes.TryRemove(ticketId, out _);
        return Task.CompletedTask;
    }
}
