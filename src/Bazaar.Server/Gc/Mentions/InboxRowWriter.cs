using Bazaar.Server.Agents.Inbox;
using Npgsql;

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
    Task<int> WriteAsync(NpgsqlTransaction tx, IReadOnlyList<InboxRow> rows, CancellationToken ct = default);
}

/// <summary>
/// SQL-first writer for planned inbox rows. Principals are agents here: an agent principal's id is its
/// <c>agent_id</c>. A principal with no <c>agent</c> row (a human) has no <c>inbox_item</c> and is skipped
/// without error, so the count returned is the number of agent inbox rows actually inserted.
/// </summary>
/// <remarks>
/// The dedupe key is <c>(agent_id, gc_id, gc_seq, kind)</c> (<c>inbox_item_gc_dedupe_idx</c>). <c>gc_seq</c> is the
/// sequence number of the message event, which is how a GC message is identified in this schema.
/// </remarks>
public sealed class InboxRowWriter(NpgsqlDataSource dataSource) : IInboxRowWriter
{
    // Inserts only when the principal is an agent; ON CONFLICT DO NOTHING makes a repeated write a no-op.
    private const string _insertSql = """
        INSERT INTO inbox_item (agent_id, kind, gc_id, gc_seq, priority)
        SELECT a.agent_id, @kind, @gc_id, @gc_seq, @priority
        FROM agent AS a
        WHERE a.agent_id = @agent_id
        ON CONFLICT DO NOTHING
        RETURNING inbox_item_id
        """;

    /// <inheritdoc />
    public async Task<int> WriteAsync(IReadOnlyList<InboxRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return 0;

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var inserted = await WriteAsync(tx, rows, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return inserted;
    }

    /// <inheritdoc />
    public async Task<int> WriteAsync(NpgsqlTransaction tx, IReadOnlyList<InboxRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(rows);

        var inserted = 0;
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            var kind = KindOf(row.Reason);

            await using var cmd = new NpgsqlCommand(_insertSql, tx.Connection, tx);
            cmd.Parameters.AddWithValue("agent_id", row.PrincipalId);
            cmd.Parameters.AddWithValue("kind", kind);
            cmd.Parameters.AddWithValue("gc_id", row.GcId);
            cmd.Parameters.AddWithValue("gc_seq", row.Seq);
            cmd.Parameters.AddWithValue("priority", checked((short)InboxPriority.Rank(kind)));

            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                inserted++;
        }

        return inserted;
    }

    private static string KindOf(InboxReason reason) => reason switch
    {
        InboxReason.Mention => InboxKind.Mention,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown inbox reason."),
    };
}
