using System.Data.Common;

namespace Bazaar.Server.Gc.Mentions;

/// <summary>
/// Persists the rows that <see cref="InboxPlanner"/> plans as <c>inbox_item</c> rows (docs/design/06-agent-runtime.md).
/// Writes are idempotent per (recipient, message): a second write of the same plan adds nothing.
/// </summary>
public interface IInboxRowWriter
{
    /// <summary>Writes the rows in their own transaction. Returns how many rows were inserted.</summary>
    Task<int> WriteAsync(IReadOnlyList<InboxRow> rows, CancellationToken ct = default);

    /// <summary>Writes the rows inside <paramref name="tx"/>, so they commit or roll back with the message event.</summary>
    Task<int> WriteAsync(DbTransaction tx, IReadOnlyList<InboxRow> rows, CancellationToken ct = default);
}
