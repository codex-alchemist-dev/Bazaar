using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Hosting;

/// <summary>
/// Logs the validated host options once at start. Secret values are never logged, only the data root and whether a KEK is
/// configured. The KEK is configured when a bootstrap reference is set or when the KEK module registered a provider; resolving
/// the provider here opens it, so a fresh install creates its key file (owner-only on POSIX) at host start (doc 14).
/// </summary>
public sealed partial class BazaarHostOptionsLogger(IOptions<BazaarHostOptions> options, ILogger<BazaarHostOptionsLogger> logger, IKekProvider? kek = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogOptionsLoaded(logger, options.Value.DataRoot, options.Value.KekReference is not null || kek is not null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Bazaar host options loaded. DataRoot={DataRoot} KekConfigured={KekConfigured}")]
    private static partial void LogOptionsLoaded(ILogger logger, string dataRoot, bool kekConfigured);
}
