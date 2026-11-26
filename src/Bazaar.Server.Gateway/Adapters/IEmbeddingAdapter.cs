using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Adapters;

/// <summary>
/// Implemented by an <see cref="IModelAdapter"/> whose wire family can embed (today only <c>openai_chat</c>, through
/// <c>POST {base}/embeddings</c>). An adapter that does not implement it is skipped for embedding calls. Like chat, an
/// upstream problem is returned as <see cref="EmbeddingOutcome.Failed"/>, never thrown; only the caller's own cancellation propagates.
/// </summary>
public interface IEmbeddingAdapter
{
    Task<EmbeddingOutcome> EmbedAsync(ProviderEndpoint endpoint, EmbeddingRequest request, CancellationToken ct);

    /// <summary>The provider default for inputs per upstream request (doc 05 "Batching and caps"); a model's <c>embed_max_batch</c> overrides it.</summary>
    int DefaultMaxBatch => 2048;

    /// <summary>
    /// The provider default for characters per input; a model's <c>embed_max_input_chars</c> overrides it. 32768 is the
    /// OpenAI-compatible limit of 8192 tokens at the gateway's four characters per token estimate.
    /// </summary>
    int DefaultMaxInputChars => 32768;
}

/// <summary>The result of one embedding attempt against one provider.</summary>
public abstract record EmbeddingOutcome
{
    /// <summary>The upstream returned one vector per input. <c>ServedModel</c> is null when the upstream did not name a model.</summary>
    public sealed record Succeeded(IReadOnlyList<float[]> Vectors, string? ServedModel, long InputTokens) : EmbeddingOutcome;

    /// <summary>The attempt failed. The failure uses the same codes as chat, so classification and fallback are shared.</summary>
    public sealed record Failed(ModelEvent.Failure Failure) : EmbeddingOutcome;
}
