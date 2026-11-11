using Npgsql;

namespace Bazaar.Server.Gc.Read;

/// <summary>Postgres-backed <see cref="IReadStateWriter"/> over <c>read_state</c>, keyed by <c>(principal_id, gc_id)</c>.</summary>
public sealed class ReadStateWriter(NpgsqlDataSource dataSource) : IReadStateWriter
{
    // One statement, so the monotonic rule is enforced by the row lock: concurrent writers serialise on the row and GREATEST
    // keeps the larger position. The SELECT from gc means an unknown GC inserts nothing and returns no row, which is reported
    // as GcNotFoundException, so no foreign-key error has to be parsed.
    private const string _markSql = """
        INSERT INTO read_state (principal_id, gc_id, last_read_seq)
        SELECT $1, g.gc_id, $3
        FROM gc g
        WHERE g.gc_id = $2
        ON CONFLICT (principal_id, gc_id) DO UPDATE
            SET last_read_seq = GREATEST(read_state.last_read_seq, EXCLUDED.last_read_seq)
        RETURNING last_read_seq
        """;

    /// <inheritdoc />
    public async Task<long> MarkReadAsync(Guid principalId, Guid gcId, long seq, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seq);

        await using var cmd = dataSource.CreateCommand(_markSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        var stored = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return stored is long position ? position : throw new GcNotFoundException(gcId);
    }
}
