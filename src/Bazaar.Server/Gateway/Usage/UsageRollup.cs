using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// Rolls <c>gw_usage</c> rows up into hourly buckets. Pure: the caller reads the rows and writes the result.
/// The job rebuilds only <see cref="WindowStart"/> onward, so a late aborted stream is absorbed. Older hours are final.
/// </summary>
public static class UsageRollup
{
    /// <summary>Hours before the current one that each run rebuilds. The current hour is always included.</summary>
    public const int RebuildHours = 2;

    /// <summary>The first hour a rebuild run covers: the current UTC hour minus <see cref="RebuildHours"/>.</summary>
    public static DateTimeOffset WindowStart(DateTimeOffset now) => FloorToHour(now).AddHours(-RebuildHours);

    /// <summary>
    /// Buckets the rows whose timestamp falls in <c>[from, to)</c>. <paramref name="from"/> is floored to the hour.
    /// Output is sorted by hour, agent, provider, model and identity, so reruns over the same rows give the same result.
    /// </summary>
    public static IReadOnlyList<UsageHourRow> Build(IEnumerable<GatewayUsageRecord> records, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(records);
        var start = FloorToHour(from);

        var buckets = new Dictionary<(DateTimeOffset Hour, string AgentId, string ProviderId, string ModelId, GatewayIdentity Identity), Accumulator>();
        foreach (var record in records)
        {
            if (record.Timestamp < start || record.Timestamp >= to)
            {
                continue;
            }

            var key = (FloorToHour(record.Timestamp), record.AgentId, record.ProviderId, record.ModelId, record.Identity);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Accumulator();
                buckets[key] = bucket;
            }

            bucket.Add(record);
        }

        return [.. buckets
            .OrderBy(pair => pair.Key.Hour)
            .ThenBy(pair => pair.Key.AgentId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.ProviderId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.ModelId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Identity)
            .Select(pair => pair.Value.ToRow(pair.Key.Hour, pair.Key.AgentId, pair.Key.ProviderId, pair.Key.ModelId, pair.Key.Identity))];
    }

    /// <summary>Floors a timestamp to the start of its UTC hour.</summary>
    public static DateTimeOffset FloorToHour(DateTimeOffset value)
    {
        var seconds = value.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(seconds - (seconds % 3600));
    }

    private sealed class Accumulator
    {
        private int _calls;
        private int _errors;
        private int _estimated;
        private GatewayTokenUsage _tokens = new(0, 0, 0, 0, 0, 0);

        public void Add(GatewayUsageRecord record)
        {
            _calls++;
            _tokens = TokenSums.Add(_tokens, record.Tokens);
            if (record.Status == UsageStatus.Error)
            {
                _errors++;
            }

            if (record.Estimated)
            {
                _estimated++;
            }
        }

        public UsageHourRow ToRow(DateTimeOffset hour, string agentId, string providerId, string modelId, GatewayIdentity identity) =>
            new(hour, agentId, providerId, modelId, identity, _calls, _tokens, _errors, _estimated);
    }
}
