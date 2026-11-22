using Bazaar.Contracts.Gateway;
using Npgsql;

namespace Bazaar.Server.Gateway.Data;

/// <summary>Quota state per provider key and model class (<c>gw_quota_state</c>), never per agent.</summary>
public sealed class QuotaStateRepository(NpgsqlDataSource dataSource)
{
    public async Task UpsertAsync(GwQuotaState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO gw_quota_state (provider_id, key_ref, model_class, state, until, observed) VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (provider_id, key_ref, model_class) DO UPDATE SET state = EXCLUDED.state, until = EXCLUDED.until, observed = EXCLUDED.observed
            """);
        cmd.Parameters.AddWithValue(state.ProviderId);
        cmd.Parameters.AddWithValue(state.KeyRef);
        cmd.Parameters.AddWithValue(state.ModelClass);
        cmd.Parameters.AddWithValue(Format(state.State));
        cmd.Parameters.AddWithValue((object?)state.Until ?? DBNull.Value);
        cmd.Parameters.Add(GatewayJson.Jsonb(state.ObservedJson));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GwQuotaState?> GetAsync(string providerId, string keyRef, string modelClass, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT provider_id, key_ref, model_class, state, until, observed::text FROM gw_quota_state WHERE provider_id = $1 AND key_ref = $2 AND model_class = $3");
        cmd.Parameters.AddWithValue(providerId);
        cmd.Parameters.AddWithValue(keyRef);
        cmd.Parameters.AddWithValue(modelClass);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<GwQuotaState>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT provider_id, key_ref, model_class, state, until, observed::text FROM gw_quota_state ORDER BY provider_id, key_ref, model_class");
        var result = new List<GwQuotaState>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    private static string Format(QuotaState s) => s switch
    {
        QuotaState.Available => "available",
        QuotaState.RateLimited => "rate_limited",
        QuotaState.SpendCapped => "spend_capped",
        QuotaState.DailyExhausted => "daily_exhausted",
        QuotaState.Down => "down",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static QuotaState Parse(string s) => s switch
    {
        "available" => QuotaState.Available,
        "rate_limited" => QuotaState.RateLimited,
        "spend_capped" => QuotaState.SpendCapped,
        "daily_exhausted" => QuotaState.DailyExhausted,
        "down" => QuotaState.Down,
        _ => throw new InvalidOperationException($"Unknown quota state '{s}'."),
    };

    private static GwQuotaState Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Parse(r.GetString(3)),
        r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4), r.GetString(5));
}
