namespace Bazaar.Server.Embeddings;

/// <summary>Reads and changes <c>embedding_profile</c> (doc 04). At most one profile is active.</summary>
public interface IEmbeddingProfileStore
{
    /// <summary>The active profile, or null when none is chosen (lexical only).</summary>
    Task<EmbeddingProfile?> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Adds an inactive profile. The dimension is fixed from here on.</summary>
    Task<EmbeddingProfile> CreateAsync(string providerId, string modelId, int dimensions, CancellationToken cancellationToken);

    /// <summary>Makes one profile the active one and deactivates the rest, in one transaction. A planned re-embed follows.</summary>
    Task ActivateAsync(Guid profileId, CancellationToken cancellationToken);
}

/// <summary>The configured providers, by provider id (the value stored in <c>embedding_profile.provider_id</c>).</summary>
public interface IEmbeddingProviderRegistry
{
    IReadOnlyCollection<string> ProviderIds { get; }

    bool TryGet(string providerId, out IEmbeddingProvider provider);
}

/// <summary>The provider and profile the install embeds with right now.</summary>
public sealed record ActiveEmbedding(EmbeddingProfile Profile, IEmbeddingProvider Provider);

/// <summary>
/// Resolves the active embedding (setting <c>memory.embedding_profile</c>, default <c>none</c>). Returns null when the setting is
/// <c>none</c> or no profile is active: lexical only. Throws <see cref="EmbeddingProfileMismatchException"/> when the configured
/// provider does not match the active profile.
/// </summary>
public interface IActiveEmbeddingSelector
{
    Task<ActiveEmbedding?> ResolveAsync(CancellationToken cancellationToken);
}

/// <summary>The current value of <c>memory.embedding_profile</c>. The host adapts its setting store onto this.</summary>
public interface IEmbeddingSettingSource
{
    Task<string> GetEmbeddingProfileSettingAsync(CancellationToken cancellationToken);
}
