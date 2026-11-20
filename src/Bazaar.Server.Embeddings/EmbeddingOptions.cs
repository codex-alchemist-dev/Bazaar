namespace Bazaar.Server.Embeddings;

/// <summary>Configuration section <c>Bazaar:Embeddings</c>: the providers an operator has set up and how the indexer runs.</summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Bazaar:Embeddings";

    /// <summary>Configured providers. <c>memory.embedding_profile</c> names one of them by <see cref="EmbeddingProviderOptions.Id"/>.</summary>
    public List<EmbeddingProviderOptions> Providers { get; set; } = [];

    /// <summary>Blocks embedded per provider call and per upsert.</summary>
    public int IndexerBatchSize { get; set; } = 32;

    /// <summary>Seconds between indexer sweeps.</summary>
    public int IndexerIntervalSeconds { get; set; } = 30;
}

/// <summary>One configured embedding provider. The dimension is fixed at setup (C39) and must match what the model returns.</summary>
public sealed class EmbeddingProviderOptions
{
    /// <summary>The id stored in <c>embedding_profile.provider_id</c> and chosen in <c>memory.embedding_profile</c>.</summary>
    public string Id { get; set; } = string.Empty;

    public EmbeddingProviderKind Kind { get; set; } = EmbeddingProviderKind.OpenAiCompatible;

    public string ModelId { get; set; } = string.Empty;

    public int Dimensions { get; set; }

    /// <summary>OpenAI-compatible only: the server root, for example <c>http://localhost:11434</c>. <c>/v1/embeddings</c> is appended.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>OpenAI-compatible only: a bootstrap secret reference (doc 03), never the key itself. Null for local runtimes.</summary>
    public string? ApiKeyReference { get; set; }

    /// <summary>Texts per HTTP request.</summary>
    public int BatchSize { get; set; } = 32;

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Gateway only: the agent id the gateway call is made under, so its policy and usage rows apply. Embedding is system work with no
    /// agent behind it, so it uses a system identity (like <c>bazaar-archive</c>), and that identity's policy must allow the install's
    /// embedding route.
    /// </summary>
    public string GatewayAgentId { get; set; } = "bazaar-embeddings";
}
