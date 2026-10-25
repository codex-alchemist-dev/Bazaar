namespace Bazaar.Server.Host;

/// <summary>Placeholder worker that logs a startup line.</summary>
public sealed partial class StartupWorker(ILogger<StartupWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bazaar.Server.Host started.")]
    private static partial void LogStarted(ILogger logger);
}
