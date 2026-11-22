using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Policy;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Writes the config-defined gateway providers (FreeLLMAPI, the owner's Anthropic key) into <c>gw_provider</c> at start. They
/// exist only as an overlay on the stored directory, but <c>gw_usage.provider_id</c> is a foreign key to that table, so without
/// the row every usage insert failed and the call went unmetered. The row carries the same values the overlay serves, so it
/// grants nothing the configuration did not already enable.
/// </summary>
public sealed class GatewayProviderRowSync(
    IGatewayDirectory directory,
    GatewayDirectoryRepository repository,
    ILogger<GatewayProviderRowSync> logger) : IHostedService
{
    private static readonly string[] _overlayProviderIds = [FreeLlmApiProvider.ProviderId, AnthropicDirectProvider.ProviderId];

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var id in _overlayProviderIds)
        {
            if (await directory.GetProviderAsync(id, cancellationToken).ConfigureAwait(false) is { } provider)
            {
                await repository.UpsertProviderAsync(provider, cancellationToken).ConfigureAwait(false);
                Log.Synced(logger, id);
            }
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Logs a usage write or event publish the gateway swallowed, so a dropped usage row is never silent.</summary>
    public static void LogInternalError(ILogger logger, Exception exception) => Log.InternalError(logger, exception);

    private static class Log
    {
        private static readonly Action<ILogger, Exception?> _internalError = LoggerMessage.Define(
            LogLevel.Error, new EventId(2, nameof(InternalError)), "The gateway could not record usage or publish an event.");

        public static void InternalError(ILogger logger, Exception exception) => _internalError(logger, exception);

        private static readonly Action<ILogger, string, Exception?> _synced = LoggerMessage.Define<string>(
            LogLevel.Information, new EventId(1, nameof(Synced)), "Gateway provider {ProviderId} written to gw_provider so its usage can be recorded.");

        public static void Synced(ILogger logger, string id) => _synced(logger, id, null);
    }
}
