namespace Bazaar.Server.Embeddings;

/// <summary>The kind of an embedding provider (doc 08, "WP6 detail"). In-process ONNX is OPEN and has no kind yet.</summary>
public enum EmbeddingProviderKind
{
    /// <summary>Deterministic hash-based vectors, for tests and development.</summary>
    Fake,

    /// <summary>Routed through the model gateway (doc 05), so per-agent policy applies.</summary>
    Gateway,

    /// <summary>HTTP <c>/v1/embeddings</c>: local runtimes (Ollama, llama.cpp, LM Studio) and API proxies.</summary>
    OpenAiCompatible,
}

/// <summary>What a provider is: its kind, model id and the length of every vector it returns.</summary>
public sealed record EmbeddingProviderDescriptor
{
    public EmbeddingProviderDescriptor(EmbeddingProviderKind kind, string modelId, int dimensions)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("A model id is required.", nameof(modelId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        Kind = kind;
        ModelId = modelId;
        Dimensions = dimensions;
    }

    public EmbeddingProviderKind Kind { get; }

    public string ModelId { get; }

    public int Dimensions { get; }
}

/// <summary>One vector per input text, in input order. Every vector has <see cref="EmbeddingProviderDescriptor.Dimensions"/> values.</summary>
public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors);

/// <summary>A row of <c>embedding_profile</c> (doc 04). One is active per install; the dimension never changes after creation.</summary>
public sealed record EmbeddingProfile(Guid ProfileId, string ProviderId, string ModelId, int Dimensions, DateTimeOffset CreatedAt, bool Active)
{
    /// <summary>True when a provider produces exactly this profile's vectors.</summary>
    public bool Matches(EmbeddingProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return string.Equals(ModelId, descriptor.ModelId, StringComparison.Ordinal) && Dimensions == descriptor.Dimensions;
    }
}

/// <summary>A provider call failed (network, HTTP status, malformed reply). The indexer defers the work; lexical search is unaffected.</summary>
public class EmbeddingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A provider does not produce the active profile's vectors. Vectors of different profiles never mix, so this is a refusal, not a retry.</summary>
public sealed class EmbeddingProfileMismatchException(string message) : EmbeddingException(message);

/// <summary>
/// The provider cannot serve the call now but may later without any change (for example the gateway's route is unset, policy denies the
/// call until an operator fixes it, or no candidate has capacity). The indexer ends the sweep with the message and the next sweep retries.
/// </summary>
public sealed class EmbeddingRetryLaterException(string message, int? retryAfterSeconds = null, Exception? inner = null) : EmbeddingException(message, inner)
{
    /// <summary>Seconds the upstream asked the caller to wait, when it said so.</summary>
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// One input is longer than the model accepts. It is a failure of that block, not of the batch: the indexer embeds the batch's blocks one
/// by one, skips the block that is too long (it is never truncated) and carries on.
/// </summary>
public sealed class EmbeddingInputTooLongException(string message, Exception? inner = null) : EmbeddingException(message, inner);
