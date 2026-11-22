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
    void Add(Guid ticketId, HiveGrantChange change);

    /// <summary>Every change still waiting for a decision.</summary>
    IReadOnlyList<(Guid TicketId, HiveGrantChange Change)> Snapshot();

    /// <summary>Forgets a ticket once it is decided or applied.</summary>
    void Remove(Guid ticketId);
}

/// <summary>
/// In-process ledger. A restart forgets pending changes: their cards expire unused and the owner asks again, so a lost entry
/// can only fail to apply, never apply something wrong.
/// </summary>
public sealed class InMemoryHiveGrantChangeLedger : IHiveGrantChangeLedger
{
    private readonly ConcurrentDictionary<Guid, HiveGrantChange> _changes = new();

    /// <inheritdoc />
    public void Add(Guid ticketId, HiveGrantChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _changes[ticketId] = change;
    }

    /// <inheritdoc />
    public IReadOnlyList<(Guid TicketId, HiveGrantChange Change)> Snapshot() =>
        [.. _changes.Select(p => (p.Key, p.Value))];

    /// <inheritdoc />
    public void Remove(Guid ticketId) => _changes.TryRemove(ticketId, out _);
}
