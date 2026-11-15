using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Jobs;

/// <summary>Runs <see cref="JobPoller"/> for the life of the host. A failed poll is retried at the next tick.</summary>
public sealed class JobPollerService(JobPoller poller) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => poller.RunAsync(stoppingToken);
}
