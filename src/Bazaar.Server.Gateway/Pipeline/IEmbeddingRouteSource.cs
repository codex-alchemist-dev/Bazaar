namespace Bazaar.Server.Gateway.Pipeline;

/// <summary>Where the gateway reads the install's default embedding route (the setting <c>gateway.embedding.route</c>, doc 05). The host backs it with the settings store, so the Gateway module needs no reference to the server's settings implementation.</summary>
public interface IEmbeddingRouteSource
{
    /// <summary>The configured route name, or an empty string when none is set.</summary>
    Task<string> GetEmbeddingRouteAsync(CancellationToken cancellationToken = default);
}
