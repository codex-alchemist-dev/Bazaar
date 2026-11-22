using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The hosted loop of the Kodo module: one pass at start, then one per <see cref="KodoServerOptions.TickInterval"/> on the injected
/// <see cref="TimeProvider"/>. Each pass runs only the probes that are due. The loop never throws into the host: a failed pass is
/// logged by the monitor and retried at the next tick, and only host shutdown ends it.
/// </summary>
internal sealed partial class KodoMonitorService(KodoMonitor monitor, KodoServerOptions options, TimeProvider time, ILogger<KodoMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (string error in options.SettingErrors)
        {
            LogSettingIgnored(logger, error);
        }

        try
        {
            TimeSpan tick = options.TickInterval < KodoServerOptions.MinimumTick ? KodoServerOptions.MinimumTick : options.TickInterval;
            using PeriodicTimer timer = new(tick, time);
            do
            {
                await monitor.RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        catch (Exception error)
        {
            // Last resort: a loop that cannot start must not take the host down.
            LogLoopStopped(logger, error);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kodo setting ignored, the default applies: {Error}")]
    private static partial void LogSettingIgnored(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Kodo loop stopped unexpectedly. Health monitoring is off until restart.")]
    private static partial void LogLoopStopped(ILogger logger, Exception error);
}
