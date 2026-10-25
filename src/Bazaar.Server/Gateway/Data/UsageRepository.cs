using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Usage;
using Npgsql;

namespace Bazaar.Server.Gateway.Data;

/// <summary>Append-only Postgres store for <c>gw_usage</c>. No prompt or completion text is ever written.</summary>
public sealed class UsageRepository(NpgsqlDataSource dataSource) : IUsageStore
{
    private const string _columns =
        "id, ts, agent_id, turn_id, provider_id, key_ref, model_id, served_model, identity, input, cache_write_5m, cache_write_1h, cache_read, output, thinking, latency_ms, status, error_code, retry_after_s, estimated, price_version";

    public async ValueTask AppendAsync(GatewayUsageRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var cmd = dataSource.CreateCommand(
            $"INSERT INTO gw_usage ({_columns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)");
        var tokens = record.Tokens;
        cmd.Parameters.AddWithValue(record.Id);
        cmd.Parameters.AddWithValue(record.Timestamp);
        cmd.Parameters.AddWithValue(record.AgentId);
        cmd.Parameters.AddWithValue((object?)record.TurnId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(record.ProviderId);
        cmd.Parameters.AddWithValue(record.KeyRef);
        cmd.Parameters.AddWithValue(record.ModelId);
        cmd.Parameters.AddWithValue((object?)record.ServedModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue(record.Identity == GatewayIdentity.Native ? "native" : "pooled");
        cmd.Parameters.AddWithValue(tokens.Input);
        cmd.Parameters.AddWithValue(tokens.CacheWrite5m);
        cmd.Parameters.AddWithValue(tokens.CacheWrite1h);
        cmd.Parameters.AddWithValue(tokens.CacheRead);
        cmd.Parameters.AddWithValue(tokens.Output);
        cmd.Parameters.AddWithValue(tokens.Thinking);
        cmd.Parameters.AddWithValue(record.LatencyMs);
        cmd.Parameters.AddWithValue(record.Status switch
        {
            UsageStatus.Success => "success",
            UsageStatus.Error => "error",
            UsageStatus.Aborted => "aborted",
            _ => throw new ArgumentOutOfRangeException(nameof(record)),
        });
        cmd.Parameters.AddWithValue((object?)record.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)record.RetryAfterSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue(record.Estimated);
        cmd.Parameters.AddWithValue((object?)record.PriceVersion ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Rows for one agent in <c>[from, to)</c>, oldest first.</summary>
    public async Task<IReadOnlyList<GatewayUsageRecord>> ListAsync(string agentId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_columns} FROM gw_usage WHERE agent_id = $1 AND ts >= $2 AND ts < $3 ORDER BY ts, id");
        cmd.Parameters.AddWithValue(agentId);
        cmd.Parameters.AddWithValue(from);
        cmd.Parameters.AddWithValue(to);
        var result = new List<GatewayUsageRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new GatewayUsageRecord(
                r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.GetString(8) == "native" ? GatewayIdentity.Native : GatewayIdentity.Pooled,
                new GatewayTokenUsage(r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13), r.GetInt64(14)),
                r.GetInt64(15),
                r.GetString(16) switch { "success" => UsageStatus.Success, "error" => UsageStatus.Error, _ => UsageStatus.Aborted },
                r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetInt32(18), r.GetBoolean(19), r.IsDBNull(20) ? null : r.GetString(20)));
        }

        return result;
    }
}
