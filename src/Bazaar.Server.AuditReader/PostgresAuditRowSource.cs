using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.AuditReader;

/// <summary>
/// Reads <c>audit_log</c> (migration common/0004) with a read-only SELECT. The payload is never selected whole: only its
/// "action" string leaves the database, so a secret in a payload cannot reach the admin panel.
/// </summary>
public sealed class PostgresAuditRowSource(NpgsqlDataSource dataSource) : IAuditRowSource
{
    private const string _sql =
        "SELECT seq, ts, actor, kind, CASE WHEN jsonb_typeof(payload) = 'object' THEN payload->>'action' END " +
        "FROM audit_log WHERE ($1::bigint IS NULL OR seq < $1) ORDER BY seq DESC LIMIT $2";

    public async Task<IReadOnlyList<AuditRawRow>> FetchAsync(long? before, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)before ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit });
        var rows = new List<AuditRawRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new AuditRawRow(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }
}
