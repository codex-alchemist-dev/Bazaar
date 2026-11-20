namespace Bazaar.Server.Embeddings;

/// <summary>Turns texts into vectors of one fixed model and dimension (doc 08, "WP6 detail").</summary>
public interface IEmbeddingProvider
{
    EmbeddingProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Embeds <paramref name="texts"/> and returns one vector per text, in order. Implementations throw
    /// <see cref="EmbeddingException"/> on failure and never return a vector of the wrong length or a wrong count.
    /// </summary>
    Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>
/// The seam the gateway implements to serve embeddings under per-agent policy (doc 05). Blocked on the Gateway work:
/// <c>IModelGateway</c> has no embeddings call yet, so nothing registers this interface today.
/// </summary>
public interface IGatewayEmbeddingClient
{
    /// <summary>Embeds the texts with the model the gateway resolves for <paramref name="modelId"/>. Returns one vector per text.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
