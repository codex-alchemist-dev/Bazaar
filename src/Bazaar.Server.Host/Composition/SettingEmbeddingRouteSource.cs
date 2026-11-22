using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Host.Composition;

/// <summary>Reads <c>gateway.embedding.route</c> from the settings store for the Gateway module.</summary>
internal sealed class SettingEmbeddingRouteSource(ISettingStore store) : IEmbeddingRouteSource
{
    public Task<string> GetEmbeddingRouteAsync(CancellationToken cancellationToken = default) =>
        store.GetAsync(GatewaySettings.EmbeddingRoute, cancellationToken: cancellationToken);
}
