using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Npgsql;

namespace Bazaar.Server.Observatory.Postgres;

/// <summary>Reads <c>gw_quota_state</c> (doc 05), one lane per provider key and model class, never per agent.</summary>
public sealed class PostgresLaneReader(NpgsqlDataSource dataSource) : ILaneReader
{
    public async ValueTask<IReadOnlyList<LaneSnapshot>> ReadAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT provider_id, model_class, state, until, observed::text FROM gw_quota_state ORDER BY provider_id, key_ref, model_class");
        var lanes = new List<LaneSnapshot>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            lanes.Add(LaneObservedParser.ToSnapshot(
                r.GetString(0), r.GetString(1), ParseState(r.GetString(2)), r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.GetString(4)));
        }

        return lanes;
    }

    public static QuotaState ParseState(string value) => value switch
    {
        "available" => QuotaState.Available,
        "rate_limited" => QuotaState.RateLimited,
        "spend_capped" => QuotaState.SpendCapped,
        "daily_exhausted" => QuotaState.DailyExhausted,
        "down" => QuotaState.Down,
        _ => throw new InvalidOperationException($"Unknown gw_quota_state.state '{value}'."),
    };
}

/// <summary>
/// Reads the <c>observed</c> jsonb of a quota row [PROPOSED format]: the last provider rate-limit figures keyed by the header
/// name with or without the <c>anthropic-ratelimit-</c> prefix (<c>requests-remaining</c>, <c>requests-limit</c>,
/// <c>input-tokens-remaining</c>, <c>input-tokens-limit</c>, <c>output-tokens-remaining</c>, <c>output-tokens-limit</c>).
/// Numbers may be JSON numbers or strings. Anything missing or unreadable is null, so the lane shows no percentage.
/// The workspace id the provider also sends is never read here (doc 23, Privacy).
/// </summary>
public static class LaneObservedParser
{
    private const string _prefix = "anthropic-ratelimit-";

    public static LaneSnapshot ToSnapshot(string providerId, string modelClass, QuotaState state, DateTimeOffset? until, string observedJson)
    {
        var values = Read(observedJson);
        return new LaneSnapshot(
            providerId, modelClass, state, until,
            Get(values, "requests-remaining"), Get(values, "requests-limit"),
            Get(values, "input-tokens-remaining"), Get(values, "input-tokens-limit"),
            Get(values, "output-tokens-remaining"), Get(values, "output-tokens-limit"));
    }

    private static long? Get(Dictionary<string, long> values, string key) => values.TryGetValue(key, out var v) ? v : null;

    private static Dictionary<string, long> Read(string json)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var name = property.Name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase) ? property.Name[_prefix.Length..] : property.Name;
                var value = property.Value;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n))
                {
                    result[name] = n;
                }
                else if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var s))
                {
                    result[name] = s;
                }
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }
}
