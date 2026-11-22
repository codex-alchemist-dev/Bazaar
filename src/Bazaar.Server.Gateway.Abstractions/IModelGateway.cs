using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway;

/// <summary>The single path every agent model call takes (docs/design/05-model-gateway.md).</summary>
public interface IModelGateway
{
    IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Embeds a batch of strings through the same route, policy, health, quota and usage path as <see cref="CallAsync"/>
    /// (docs/design/05-model-gateway.md, Embeddings). The result has one vector per input, in input order. An empty batch
    /// returns an empty result without a call. A call that cannot be served (policy denial, no candidate that can embed,
    /// or an upstream failure on every candidate) throws <see cref="Ai.GatewayModelException"/> with the gateway's failure code.
    /// </summary>
    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken);
}
