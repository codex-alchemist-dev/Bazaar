using Bazaar.Server.Vault.Search;

namespace Bazaar.Server.Embeddings;

/// <summary>Makes any <see cref="IEmbeddingProvider"/> the query-side embedder Vault search accepts, bound to one profile.</summary>
public sealed class VaultQueryEmbedder : IVaultQueryEmbedder
{
    private readonly IEmbeddingProvider _provider;

    public VaultQueryEmbedder(EmbeddingProfile profile, IEmbeddingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(provider);
        if (!profile.Matches(provider.Descriptor))
        {
            throw new EmbeddingProfileMismatchException(
                $"Provider model '{provider.Descriptor.ModelId}' ({provider.Descriptor.Dimensions} dimensions) does not match profile '{profile.ProfileId}'.");
        }

        ProfileId = profile.ProfileId;
        _provider = provider;
    }

    public Guid ProfileId { get; }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = await _provider.EmbedAsync([text], cancellationToken).ConfigureAwait(false);
        return result.Vectors[0];
    }
}
