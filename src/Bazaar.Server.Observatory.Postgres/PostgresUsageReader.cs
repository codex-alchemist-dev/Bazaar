using Bazaar.Contracts.Gateway;
using Npgsql;

namespace Bazaar.Server.Observatory.Postgres;

/// <summary>
/// Reads <c>gw_usage</c> for the Observatory (doc 23). Rows hidden by retention (<c>soft_deleted_at</c>, added by the gateway
/// retention migration) are skipped when that column exists, so this works before and after that migration. Policy-denied rows
/// (<c>status = 'denied'</c>, zero tokens, no provider call) are left out: they are not usage and have no price.
/// </summary>
public sealed class PostgresUsageReader(NpgsqlDataSource dataSource) : IUsageReader
{
    private const string _columns =
        "id, ts, agent_id, turn_id, provider_id, key_ref, model_id, served_model, identity, input, cache_write_5m, cache_write_1h, cache_read, output, thinking, latency_ms, status, error_code, retry_after_s, estimated, price_version";

    private const string _hasTombstoneSql =
        "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'gw_usage' AND column_name = 'soft_deleted_at')";

    private bool? _hasTombstone;

    public async ValueTask<IReadOnlyList<GatewayUsageRecord>> ReadRangeAsync(DateTimeOffset start, DateTimeOffset until, CancellationToken ct)
    {
        var filter = await FilterAsync(ct).ConfigureAwait(false);
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM gw_usage WHERE ts >= $1 AND ts < $2 {filter} ORDER BY ts, id");
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = start.UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = until.UtcDateTime });
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<GatewayUsageRecord>> ReadTurnAsync(string turnId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        var filter = await FilterAsync(ct).ConfigureAwait(false);
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM gw_usage WHERE turn_id = $1 {filter} ORDER BY ts, id");
        cmd.Parameters.AddWithValue(turnId);
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    private async ValueTask<string> FilterAsync(CancellationToken ct)
    {
        if (_hasTombstone is null)
        {
            await using var probe = dataSource.CreateCommand(_hasTombstoneSql);
            _hasTombstone = (bool)(await probe.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        return _hasTombstone.Value ? "AND status <> 'denied' AND soft_deleted_at IS NULL" : "AND status <> 'denied'";
    }

    private static async Task<IReadOnlyList<GatewayUsageRecord>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<GatewayUsageRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new GatewayUsageRecord(
                r.GetGuid(0),
                r.GetFieldValue<DateTimeOffset>(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4),
                r.GetString(5),
                r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.GetString(8) == "native" ? GatewayIdentity.Native : GatewayIdentity.Pooled,
                new GatewayTokenUsage(r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13), r.GetInt64(14)),
                r.GetInt64(15),
                r.GetString(16) switch { "success" => UsageStatus.Success, "error" => UsageStatus.Error, _ => UsageStatus.Aborted },
                r.IsDBNull(17) ? null : r.GetString(17),
                r.IsDBNull(18) ? null : r.GetInt32(18),
                r.GetBoolean(19),
                r.IsDBNull(20) ? null : r.GetString(20)));
        }

        return rows;
    }
}
