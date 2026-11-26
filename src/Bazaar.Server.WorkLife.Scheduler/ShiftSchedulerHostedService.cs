using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.WorkLife.Scheduler;

/// <summary>
/// Runs <see cref="ShiftScheduler.TickAsync"/> once at start and then every <see cref="ShiftSchedulerOptions.TickInterval"/> on the
/// injected <see cref="TimeProvider"/>. A tick that throws is logged and the loop carries on.
/// </summary>
public sealed class ShiftSchedulerHostedService(
    ShiftScheduler scheduler,
    ShiftSchedulerOptions options,
    TimeProvider time,
    ILogger<ShiftSchedulerHostedService>? logger = null) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            await SafeTickAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(options.TickInterval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SafeTickAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task SafeTickAsync(CancellationToken ct)
    {
        try
        {
            await scheduler.TickAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            SchedulerLog.TickFailed((ILogger?)logger ?? NullLogger.Instance, ex);
        }
    }
}
