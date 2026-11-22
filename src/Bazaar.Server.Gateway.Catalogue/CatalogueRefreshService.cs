using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>
/// Runs the scheduled catalogue refresh at <c>gateway.catalogue.refresh_hours</c>. 0 (the default) means no scheduled run: the
/// release update and the manual check are the only refreshes. The setting is re-read each cycle, so a change applies without a restart.
/// </summary>
public sealed partial class CatalogueRefreshService(
    ICatalogueRefresher refresher,
    ICatalogueSchedule schedule,
    ILogger<CatalogueRefreshService> logger,
    TimeProvider? time = null) : BackgroundService
{
    /// <summary>How often a disabled schedule is re-read to notice a change.</summary>
    public static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastRun = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hours = await schedule.GetRefreshHoursAsync(stoppingToken).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                if (hours > 0 && (lastRun == DateTimeOffset.MinValue || now - lastRun >= TimeSpan.FromHours(hours)))
                {
                    await refresher.RefreshAllAsync(stoppingToken).ConfigureAwait(false);
                    lastRun = _time.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRefreshFailed(logger, ex);
            }

            try
            {
                await Task.Delay(IdlePoll, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The scheduled catalogue refresh failed; it will be tried again at the next cycle.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
