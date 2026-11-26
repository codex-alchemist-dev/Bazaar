using Npgsql;

namespace Bazaar.Server.AuditSinks.Postgres;

/// <summary>
/// Durable per-sink delivery cursor over <c>audit_sink_cursor</c> (auditsinks migration 0001). A sink with no row starts at
/// <see cref="AuditSinkCursor.Start"/>. Saving never moves a cursor backwards: each column keeps the larger of the stored and
/// the offered value, in one statement.
/// </summary>
public sealed class PostgresAuditSinkCursorStore(NpgsqlDataSource dataSource) : IAuditSinkCursorStore
{
    private const string _select = "SELECT last_row_seq, last_checkpoint_seq FROM audit_sink_cursor WHERE sink_name = $1";

    private const string _upsert =
        "INSERT INTO audit_sink_cursor (sink_name, last_row_seq, last_checkpoint_seq) VALUES ($1, $2, $3) " +
        "ON CONFLICT (sink_name) DO UPDATE SET " +
        "last_row_seq = GREATEST(audit_sink_cursor.last_row_seq, EXCLUDED.last_row_seq), " +
        "last_checkpoint_seq = GREATEST(audit_sink_cursor.last_checkpoint_seq, EXCLUDED.last_checkpoint_seq), " +
        "updated_at = now()";

    public async Task<AuditSinkCursor> GetAsync(string sinkName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sinkName);
        await using var cmd = dataSource.CreateCommand(_select);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = sinkName });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new AuditSinkCursor(reader.GetInt64(0), reader.GetInt64(1))
            : AuditSinkCursor.Start;
    }

    public async Task SaveAsync(string sinkName, AuditSinkCursor cursor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sinkName);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor.LastRowSeq);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor.LastCheckpointSeq);
        await using var cmd = dataSource.CreateCommand(_upsert);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = sinkName });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cursor.LastRowSeq });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cursor.LastCheckpointSeq });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
