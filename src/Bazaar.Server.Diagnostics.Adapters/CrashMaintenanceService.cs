using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Diagnostics.Adapters;

/// <summary>Runs the filing outbox and the retention purge on a timer. A failed pass is logged and tried again on the next tick.</summary>
public sealed partial class CrashMaintenanceService(
    CrashActionDispatcher dispatcher,
    CrashDiagnosisService diagnosis,
    ICrashRetention retention,
    CrashRetentionPolicy policy,
    CrashFilingOptions options,
    TimeProvider clock,
    ILogger<CrashMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _purgeEvery = TimeSpan.FromHours(6);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurge = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(_interval, clock);
        do
        {
            await RunOnceAsync(nextPurge, stoppingToken).ConfigureAwait(false);
            if (clock.GetUtcNow() >= nextPurge)
            {
                nextPurge = clock.GetUtcNow() + _purgeEvery;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task RunOnceAsync(DateTimeOffset nextPurge, CancellationToken ct)
    {
        try
        {
            if (options.FilingEnabled)
            {
                _ = await dispatcher.RunOnceAsync(ct: ct).ConfigureAwait(false);
            }

            _ = await diagnosis.RunAutoAsync(ct: ct).ConfigureAwait(false);

            if (clock.GetUtcNow() >= nextPurge)
            {
                _ = await retention.PurgeAsync(clock.GetUtcNow(), policy, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPassFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Crash maintenance pass failed; it will run again on the next tick.")]
    private partial void LogPassFailed(Exception exception);
}
