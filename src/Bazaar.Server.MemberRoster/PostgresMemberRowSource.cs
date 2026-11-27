using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.MemberRoster;

/// <summary>
/// Read-only SELECT over <c>agent</c>, <c>agent_model_policy</c>, <c>turn</c> and <c>gw_usage</c> (common migrations) and
/// <c>human</c>, <c>device</c> and <c>refresh_credential</c> (remote migration). No secret column is selected. An agent's last
/// seen is its newest turn; a human's is the newest refresh credential issued to any of their devices, falling back to the
/// newest device pairing. Usage is input plus output tokens in the last 24 hours.
/// </summary>
public sealed class PostgresMemberRowSource(NpgsqlDataSource dataSource) : IMemberRowSource
{
    private const string _sql = """
        WITH members AS (
            SELECT a.agent_id AS id, a.name AS name, 'agent' AS kind, 'agent' AS role, a.state AS status,
                   (SELECT max(t.started_at) FROM turn t WHERE t.agent_id = a.agent_id) AS last_seen,
                   (SELECT COALESCE(p.default_route, p.allowed->>0) FROM agent_model_policy p WHERE p.agent_id = a.agent_id::text) AS model,
                   (SELECT COALESCE(sum(u.input + u.output), 0) FROM gw_usage u
                     WHERE u.agent_id = a.agent_id::text AND u.ts >= now() - interval '24 hours')::bigint AS tokens24h
            FROM agent a
            UNION ALL
            SELECT h.id, h.display_name, 'human', h.role, h.status,
                   (SELECT max(COALESCE((SELECT max(rc.issued_at) FROM refresh_credential rc WHERE rc.device_id = d.id), d.created_at))
                      FROM device d WHERE d.human_id = h.id),
                   NULL, 0::bigint
            FROM human h
        )
        SELECT id, name, kind, role, status, last_seen, model, tokens24h, count(*) OVER () AS total
        FROM members
        WHERE ($1::text IS NULL OR kind = $1)
        ORDER BY kind, name, id
        LIMIT $2 OFFSET $3
        """;

    public async Task<MemberRawPage> FetchAsync(string? kind, int limit, int offset, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)kind ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = offset });
        var rows = new List<MemberRawRow>();
        long total = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new MemberRawRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(7)));
            total = reader.GetInt64(8);
        }

        return new MemberRawPage(rows, total);
    }
}
