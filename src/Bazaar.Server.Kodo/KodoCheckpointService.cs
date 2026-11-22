using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The hosted loop that runs <see cref="KodoCheckpointScheduler"/> on the injected <see cref="TimeProvider"/>. Registered only when
/// <see cref="KodoServerOptions.CheckpointsEnabled"/> is true. It never throws into the host.
/// </summary>
internal sealed partial class KodoCheckpointService(KodoCheckpointScheduler? scheduler, KodoServerOptions options, TimeProvider time, ILogger<KodoCheckpointService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (scheduler is null)
        {
            LogNoStore(logger);
            return;
        }

        try
        {
            TimeSpan tick = options.TickInterval < KodoServerOptions.MinimumTick ? KodoServerOptions.MinimumTick : options.TickInterval;
            using PeriodicTimer timer = new(tick, time);
            do
            {
                await scheduler.RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        catch (Exception error)
        {
            LogLoopStopped(logger, error);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kodo checkpoints are enabled but no journal store is registered. No checkpoints are cut.")]
    private static partial void LogNoStore(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Kodo checkpoint loop stopped unexpectedly. No checkpoints are cut until restart.")]
    private static partial void LogLoopStopped(ILogger logger, Exception error);
}
