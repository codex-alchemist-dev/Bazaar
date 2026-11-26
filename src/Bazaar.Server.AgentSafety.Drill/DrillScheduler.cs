using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>
/// Runs the drill on an interval (weekly by default) and stores each run. A failed drill is logged and stored; a drill that cannot run
/// (a global stop, a bad target, a failing store) is logged and the next run is still scheduled, so the host never stops for it.
/// </summary>
public sealed partial class DrillScheduler(
    DrillRunner runner,
    IDrillResultStore store,
    IDrillTarget target,
    IOptions<DrillOptions> options,
    ILogger<DrillScheduler> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeSpan _interval = options.Value.Interval < DrillOptions.MinimumInterval ? DrillOptions.MinimumInterval : options.Value.Interval;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs the drill once and stores the result. Every failure is logged here, except a host shutdown.</summary>
    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var result = await runner.RunAsync(target.DrillAgentId, ct).ConfigureAwait(false);
            await store.AddAsync(result, ct).ConfigureAwait(false);
            if (!result.Passed)
            {
                LogDrillFailed(logger, string.Join(", ", result.Steps.Where(s => !s.Passed).Select(s => s.Name)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDrillNotRun(logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Kill switch drill failed at step(s): {FailedSteps}.")]
    private static partial void LogDrillFailed(ILogger logger, string failedSteps);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kill switch drill did not run ({ExceptionType}).")]
    private static partial void LogDrillNotRun(ILogger logger, string exceptionType);
}
