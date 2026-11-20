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
