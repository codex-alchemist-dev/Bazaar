namespace Bazaar.Server.Embeddings;

/// <summary>
/// Picks the provider for the active profile. <c>none</c> (the default) or no active profile means lexical only. A provider that does not
/// produce the active profile's vectors is refused, so vectors of different profiles never mix (doc 08, "WP6 detail").
/// </summary>
public sealed class ActiveEmbeddingSelector(
    IEmbeddingSettingSource setting,
    IEmbeddingProfileStore profiles,
    IEmbeddingProviderRegistry registry) : IActiveEmbeddingSelector
{
    public async Task<ActiveEmbedding?> ResolveAsync(CancellationToken cancellationToken)
    {
        var chosen = await setting.GetEmbeddingProfileSettingAsync(cancellationToken).ConfigureAwait(false);
        if (EmbeddingSettings.IsNone(chosen))
        {
            return null;
        }

        var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        if (!string.Equals(profile.ProviderId, chosen, StringComparison.Ordinal))
        {
            throw new EmbeddingProfileMismatchException(
                $"The setting names provider '{chosen}' but the active profile belongs to '{profile.ProviderId}'.");
        }

        if (!registry.TryGet(chosen, out var provider))
        {
            throw new EmbeddingProfileMismatchException($"Provider '{chosen}' is not configured.");
        }

        if (!profile.Matches(provider.Descriptor))
        {
            throw new EmbeddingProfileMismatchException(
                $"Provider '{chosen}' produces model '{provider.Descriptor.ModelId}' at {provider.Descriptor.Dimensions} dimensions; "
                + $"the active profile is '{profile.ModelId}' at {profile.Dimensions}. Changing profile is a planned re-embed.");
        }

        return new ActiveEmbedding(profile, provider);
    }
}
