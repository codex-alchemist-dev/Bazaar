using Bazaar.Server.Security.Budgets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.AgentSafety.Persistence;

/// <summary>
/// The sweeper job of doc 48, DECIDED 11.4 ("Writing"): removes large token amounts that were staged but never sealed once
/// they are older than <c>agent.budget.large_amount_staging_hours</c>. Runs every <see cref="Interval"/>, first one interval
/// after start, on the injected <see cref="TimeProvider"/>. A failed sweep is logged and retried on the next tick; it never
/// stops the host.
/// </summary>
public sealed partial class LargeTokenAmountSweeper(
    PostgresLargeTokenAmountStore store, TokenBudgetOptions options, TimeProvider time, ILogger<LargeTokenAmountSweeper> logger) : BackgroundService
{
    /// <summary>Time between sweeps.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private PeriodicTimer? _timer;

    /// <summary>Raised after each sweep with the number of staged amounts removed; for tests and diagnostics.</summary>
    public event Action<int>? Swept;

    /// <summary>One sweep: removes staged amounts created more than the staging age before now. Returns how many.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct = default)
    {
        var removed = await store.SweepStagedAsync(time.GetUtcNow() - options.LargeAmountStagingAge, ct).ConfigureAwait(false);
        if (removed > 0)
        {
            LogSwept(removed);
        }

        Swept?.Invoke(removed);
        return removed;
    }

    /// <inheritdoc />
    /// <remarks>The timer starts here, before the loop runs, so the first sweep is exactly one interval after start.</remarks>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _timer ??= new PeriodicTimer(Interval, time);
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = _timer ?? throw new InvalidOperationException("The sweeper was not started.");
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                LogSweepFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} staged large token amounts that were never sealed.")]
    private partial void LogSwept(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The large token amount sweep failed; it runs again on the next tick.")]
    private partial void LogSweepFailed(Exception exception);
}
