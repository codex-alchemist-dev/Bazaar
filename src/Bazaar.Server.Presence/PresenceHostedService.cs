using Bazaar.Server.Presence.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Presence;

/// <summary>
/// Rebuilds presence rows at start from the seed source (availability and open turns, docs/design/23 "Restart") when one is
/// registered, then sweeps on a timer so a row with no turn event for ten minutes shows <c>unknown</c>.
/// </summary>
public sealed class PresenceHostedService(
    ActivityProjector projector,
    PresenceOptions options,
    TimeProvider time,
    IPresenceSeedSource? seeds = null) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (seeds is not null)
            {
                await projector.RebuildAsync(await seeds.LoadAsync(stoppingToken).ConfigureAwait(false), stoppingToken).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(options.SweepInterval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await projector.SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
