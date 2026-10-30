using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Runs at start when dev auth is enabled. Resolving the authenticator resolves the token secret, so a bad reference fails the start,
/// and a WARNING is logged that the server accepts a local token as owner.
/// </summary>
public sealed partial class DevAuthStartupCheck(IConnectionAuthenticator authenticator, ILogger<DevAuthStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (authenticator is not DevTokenAuthenticator)
        {
            throw new InvalidOperationException($"{DevAuthOptions.SectionName} is enabled but another connection authenticator is registered.");
        }

        LogDevAuthOn(logger, DevAuthOptions.SectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dev authentication is ON: one bearer token from {Section} grants the owner principal. Local development only; never expose this server.")]
    private static partial void LogDevAuthOn(ILogger logger, string section);
}
