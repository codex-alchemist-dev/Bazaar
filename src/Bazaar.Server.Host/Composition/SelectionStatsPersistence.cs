using Bazaar.Server.Gateway.Selection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Loads the selector's learned counters at start and saves them every interval and at stop, so a restart does not start from
/// priors (docs/design/92-endpoint-selection.md, open question 3). Only runs when <c>Bazaar:Gateway:Selection:StatsPath</c> is set.
/// A failed save is skipped and tried again; it never stops the host.
/// </summary>
public sealed class SelectionStatsPersistence(IPersistentStats stats, string? path, TimeSpan interval) : BackgroundService
{
    private bool Enabled => !string.IsNullOrWhiteSpace(path);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Enabled)
        {
            stats.Import(StatsFile.Load(path!));
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Save();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping: the final save happens in StopAsync.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        Save();
    }

    private void Save()
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            StatsFile.Save(path!, stats.Export());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Try again at the next tick.
        }
    }
}
