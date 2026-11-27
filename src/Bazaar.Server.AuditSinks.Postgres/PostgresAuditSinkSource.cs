using Npgsql;

namespace Bazaar.Server.AuditSinks.Postgres;

/// <summary>
/// Read side of the committed chain for the dispatcher: <c>audit_log</c> rows and <c>audit_checkpoint</c> rows, read only.
/// Give it the raw <see cref="NpgsqlDataSource"/>, not the audited one, so reading the chain does not itself write audit rows
/// (read-audit recursion). Rows are returned as stored: the payload is the jsonb text, so personal-field ciphertext stays
/// ciphertext. This class only maps columns; it never verifies or recomputes a hash.
/// </summary>
public sealed class PostgresAuditSinkSource(NpgsqlDataSource dataSource) : IAuditSinkSource
{
    private const string _rows =
        "SELECT seq, ts, actor, kind, payload::text, prev_hash, hash FROM audit_log WHERE seq > $1 ORDER BY seq LIMIT $2";

    private const string _checkpoints =
        "SELECT first_seq, last_seq, row_count, chain_head_hash, merkle_root, created_at, key_id, signature " +
        "FROM audit_checkpoint WHERE last_seq > $1 ORDER BY last_seq LIMIT $2";

    public async Task<IReadOnlyList<AuditSinkRow>> ReadRowsAfterAsync(long afterSeq, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSeq);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var cmd = dataSource.CreateCommand(_rows);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = afterSeq });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<AuditSinkRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new AuditSinkRow(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<byte[]>(5),
                reader.GetFieldValue<byte[]>(6)));
        }

        return result;
    }

    public async Task<IReadOnlyList<AuditSinkCheckpoint>> ReadCheckpointsAfterAsync(long afterLastSeq, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterLastSeq);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var cmd = dataSource.CreateCommand(_checkpoints);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = afterLastSeq });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<AuditSinkCheckpoint>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new AuditSinkCheckpoint(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetFieldValue<byte[]>(3),
                reader.GetFieldValue<byte[]>(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6),
                reader.GetFieldValue<byte[]>(7)));
        }

        return result;
    }
}
