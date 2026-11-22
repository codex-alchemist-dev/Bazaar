using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Hosting;

/// <summary>Logs the validated host options once at start. Secret values are never logged, only the data root and whether a KEK reference is set.</summary>
public sealed partial class BazaarHostOptionsLogger(IOptions<BazaarHostOptions> options, ILogger<BazaarHostOptionsLogger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogOptionsLoaded(logger, options.Value.DataRoot, options.Value.KekReference is not null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Bazaar host options loaded. DataRoot={DataRoot} KekConfigured={KekConfigured}")]
    private static partial void LogOptionsLoaded(ILogger logger, string dataRoot, bool kekConfigured);
}
