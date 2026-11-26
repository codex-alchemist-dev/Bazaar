using Bazaar.Server.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Health;

/// <summary>
/// The raw retention sweep (doc 25, setting <c>metrics.raw_retention_days</c>). It deletes raw health reports older than the
/// retention, counted in UTC days from today. The derived <c>metric_samples</c> are rollups and stay. Idempotent: a re-run deletes nothing new.
/// </summary>
public sealed partial class HealthRetentionJobHandler(
    IHealthReportStore reports,
    IRawRetentionSource retention,
    TimeProvider timeProvider,
    ILogger<HealthRetentionJobHandler>? logger = null) : IJobHandler
{
    /// <summary>The <c>job.kind</c> the poller enqueues on the sweep cadence.</summary>
    public const string JobKind = "health.retention";

    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    [LoggerMessage(Level = LogLevel.Information, Message = "Health retention removed {Count} raw reports older than {Cutoff}.")]
    private static partial void LogPruned(ILogger logger, int count, DateOnly cutoff);

    public string Kind => JobKind;

    public async Task HandleAsync(JobRecord job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await SweepAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Runs one sweep and returns how many reports were removed. Exposed for tests.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var days = Math.Clamp(await retention.GetDaysAsync(ct).ConfigureAwait(false), RawRetention.MinDays, RawRetention.MaxDays);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var cutoff = today.AddDays(-days);
        var removed = await reports.PruneBeforeAsync(cutoff, ct).ConfigureAwait(false);
        if (removed > 0)
            LogPruned(_log, removed, cutoff);
        return removed;
    }
}
