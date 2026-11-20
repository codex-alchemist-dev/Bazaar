using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Council.Service;

/// <summary>
/// Runs <see cref="CouncilDeadlineSweeper.RunOnceAsync"/> at start and then every <see cref="CouncilSweepOptions.Interval"/>.
/// Holds no logic of its own: the pass, its counts and its error handling live in the sweeper.
/// </summary>
public sealed partial class CouncilDeadlineWorker(
    CouncilDeadlineSweeper sweeper,
    CouncilSweepOptions options,
    TimeProvider clock,
    ILogger<CouncilDeadlineWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sweeper.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed pass (for example, the store is down) is retried at the next interval.
                LogPassFailed(logger, ex);
            }

            try
            {
                await Task.Delay(options.Interval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Council deadline sweep pass failed.")]
    private static partial void LogPassFailed(ILogger logger, Exception ex);
}
