namespace Bazaar.Server.Embeddings;

/// <summary>
/// Embeddings through the model gateway (doc 05), so per-agent policy and usage records apply. Blocked on the Gateway work: it needs
/// an <see cref="IGatewayEmbeddingClient"/>, and the gateway does not provide one yet.
/// </summary>
public sealed class GatewayEmbeddingProvider : IEmbeddingProvider
{
    private readonly IGatewayEmbeddingClient _client;

    public GatewayEmbeddingProvider(IGatewayEmbeddingClient client, EmbeddingProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        Descriptor = new EmbeddingProviderDescriptor(EmbeddingProviderKind.Gateway, options.ModelId, options.Dimensions);
    }

    public EmbeddingProviderDescriptor Descriptor { get; }

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var vectors = await _client.EmbedAsync(Descriptor.ModelId, texts, cancellationToken).ConfigureAwait(false);
        if (vectors.Count != texts.Count || vectors.Any(v => v.Length != Descriptor.Dimensions))
        {
            throw new EmbeddingException("The gateway returned embeddings with the wrong count or dimension.");
        }

        return new EmbeddingResult(vectors);
    }
}
