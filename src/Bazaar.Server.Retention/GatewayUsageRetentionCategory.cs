using Bazaar.Server.Gateway;
using Bazaar.Server.Settings;
using Npgsql;

namespace Bazaar.Server.Retention;

/// <summary>
/// Retention for <c>gw_usage</c> (doc 05): rows older than <c>gateway.usage.retention_days</c> are deleted, 0 meaning indefinite.
/// Usage rows are keyed by agent, not person, so no person hold applies; non-person holds still block the sweep.
/// </summary>
public sealed class GatewayUsageRetentionCategory(NpgsqlDataSource dataSource, ISettingStore settings) : IRetentionCategory
{
    public const string CategoryName = "gateway_usage";

    // Oldest rows first, so a bounded run always makes progress from the tail.
    private const string _sql = "DELETE FROM gw_usage WHERE id IN (SELECT id FROM gw_usage WHERE ts < $1 ORDER BY ts LIMIT $2)";

    public string Name => CategoryName;

    public async Task<TimeSpan?> GetPeriodAsync(CancellationToken ct)
    {
        var days = await settings.GetAsync(GatewaySettings.UsageRetentionDays, cancellationToken: ct).ConfigureAwait(false);
        return days <= 0 ? null : TimeSpan.FromDays(days);
    }

    public Task<int> GetBatchSizeAsync(CancellationToken ct) =>
        settings.GetAsync(GatewaySettings.UsagePurgeBatchSize, cancellationToken: ct);

    public Task<IReadOnlyCollection<Guid>> GetCoveredPersonsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<Guid>>([]);

    public async Task<int> PurgeBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = cutoff.UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
