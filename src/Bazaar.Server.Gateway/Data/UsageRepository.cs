using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Numerics;
using Bazaar.Server.Gateway.Usage;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Gateway.Data;

/// <summary>
/// Append-only Postgres store for <c>gw_usage</c>. No prompt or completion text is ever written.
/// <para>
/// The six token-count columns are unconstrained <c>numeric</c> (doc 48, DECIDED 11.3; migration <c>gateway/0001</c>). Counts are
/// sent as text cast to <c>numeric</c> and read back as <c>::text</c> and parsed with <see cref="TokenAmount"/>, because the
/// driver's <c>decimal</c> and big-integer mappings cannot carry every size. A count outside
/// <see cref="TokenAmountBounds.PostgresNumeric"/> cannot be stored: <see cref="AppendAsync"/> refuses the row and writes nothing.
/// </para>
/// </summary>
public sealed class UsageRepository(NpgsqlDataSource dataSource) : IUsageStore
{
    private const string _columns =
        "id, ts, agent_id, turn_id, provider_id, key_ref, model_id, served_model, identity, input, cache_write_5m, cache_write_1h, cache_read, output, thinking, latency_ms, status, error_code, retry_after_s, estimated, price_version";

    // The same list for reads, with the token counts as text so no size is lost on the way out.
    private const string _selectColumns =
        "id, ts, agent_id, turn_id, provider_id, key_ref, model_id, served_model, identity, input::text, cache_write_5m::text, cache_write_1h::text, cache_read::text, output::text, thinking::text, latency_ms, status, error_code, retry_after_s, estimated, price_version";

    public async ValueTask AppendAsync(GatewayUsageRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var cmd = dataSource.CreateCommand(
            $"INSERT INTO gw_usage ({_columns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::numeric, $11::numeric, $12::numeric, $13::numeric, $14::numeric, $15::numeric, $16, $17, $18, $19, $20, $21)");
        var tokens = record.Tokens;
        if (!AllFit(tokens))
        {
            throw new ArgumentOutOfRangeException(nameof(record), "A token count has more digits than a numeric column can store; the row is not written.");
        }

        cmd.Parameters.AddWithValue(record.Id);
        cmd.Parameters.AddWithValue(record.Timestamp);
        cmd.Parameters.AddWithValue(record.AgentId);
        cmd.Parameters.AddWithValue((object?)record.TurnId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(record.ProviderId);
        cmd.Parameters.AddWithValue(record.KeyRef);
        cmd.Parameters.AddWithValue(record.ModelId);
        cmd.Parameters.AddWithValue((object?)record.ServedModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue(record.Identity == GatewayIdentity.Native ? "native" : "pooled");
        cmd.Parameters.Add(Amount(tokens.Input));
        cmd.Parameters.Add(Amount(tokens.CacheWrite5m));
        cmd.Parameters.Add(Amount(tokens.CacheWrite1h));
        cmd.Parameters.Add(Amount(tokens.CacheRead));
        cmd.Parameters.Add(Amount(tokens.Output));
        cmd.Parameters.Add(Amount(tokens.Thinking));
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
            $"SELECT {_selectColumns} FROM gw_usage WHERE agent_id = $1 AND ts >= $2 AND ts < $3 ORDER BY ts, id");
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
                new GatewayTokenUsage(Amount(r, 9), Amount(r, 10), Amount(r, 11), Amount(r, 12), Amount(r, 13), Amount(r, 14)),
                r.GetInt64(15),
                r.GetString(16) switch { "success" => UsageStatus.Success, "error" => UsageStatus.Error, _ => UsageStatus.Aborted },
                r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetInt32(18), r.GetBoolean(19), r.IsDBNull(20) ? null : r.GetString(20)));
        }

        return result;
    }

    private static bool AllFit(GatewayTokenUsage t) =>
        t.Input.Fits(TokenAmountBounds.PostgresNumeric)
        && t.CacheWrite5m.Fits(TokenAmountBounds.PostgresNumeric)
        && t.CacheWrite1h.Fits(TokenAmountBounds.PostgresNumeric)
        && t.CacheRead.Fits(TokenAmountBounds.PostgresNumeric)
        && t.Output.Fits(TokenAmountBounds.PostgresNumeric)
        && t.Thinking.Fits(TokenAmountBounds.PostgresNumeric);

    /// <summary>A count as a text parameter; the SQL casts it to <c>numeric</c>.</summary>
    private static NpgsqlParameter<string> Amount(TokenAmount tokens) =>
        new() { TypedValue = tokens.ToString(), NpgsqlDbType = NpgsqlDbType.Text };

    /// <summary>One stored count read as text. The column's CHECK and the numeric limits mean it always parses.</summary>
    private static TokenAmount Amount(NpgsqlDataReader reader, int ordinal) => TokenAmount.Parse(reader.GetString(ordinal));
}
