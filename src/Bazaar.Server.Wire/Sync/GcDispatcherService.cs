using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Wire.Sync;

/// <summary>Runs the <see cref="GcDispatcher"/> loop for the life of the host, fed by the GC notification source.</summary>
public sealed class GcDispatcherService(
    GcDispatcher dispatcher,
    IGcNotificationSource notifications,
    IOptions<SyncOptions> options) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        dispatcher.RunAsync(notifications, options.Value.SweepInterval, stoppingToken);
}
