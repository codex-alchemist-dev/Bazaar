using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Agents.Scheduling;

/// <summary>
/// The hosted wake loop. It runs one <see cref="IAgentScheduler"/> pass when the host starts and then one pass every
/// <see cref="AgentSchedulerOptions.PollInterval"/>, for the life of the host. Registered only when the wake loop is enabled.
/// </summary>
/// <remarks>
/// Passes never overlap: the next pass starts after the last one returned, at the next tick. A pass that throws is logged and
/// the loop goes on at the next tick, so a failing pass never stops the host. Stopping the host cancels the pass in flight.
/// </remarks>
public sealed partial class AgentSchedulerService(
    IAgentScheduler scheduler,
    AgentSchedulerOptions options,
    TimeProvider time,
    ILogger<AgentSchedulerService> logger) : BackgroundService
{
    private readonly ILogger<AgentSchedulerService> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(options.PollInterval, time);
            do
            {
                await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Runs one pass. Throws only when the host is stopping.</summary>
    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        try
        {
            var pass = await scheduler.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            if (pass.AgentsDue > 0)
                LogPass(pass.AgentsDue, pass.TurnsStarted, pass.SkippedByGuard, pass.SkippedLeaseNotAcquired, pass.SkippedNothingDue, pass.Errors);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPassFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent wake loop: {AgentsDue} agents due, {TurnsStarted} turns started, {SkippedByGuard} skipped by the loop guard, {SkippedLeaseNotAcquired} skipped for a held lease, {SkippedNothingDue} with nothing due, {Errors} errors.")]
    private partial void LogPass(int agentsDue, int turnsStarted, int skippedByGuard, int skippedLeaseNotAcquired, int skippedNothingDue, int errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent wake loop: a pass failed. The next tick tries again.")]
    private partial void LogPassFailed(Exception ex);
}
