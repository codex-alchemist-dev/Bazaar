namespace Bazaar.Server.Embeddings;

/// <summary>
/// Picks the provider for the active profile. <c>none</c> (the default) or no active profile means lexical only. A provider that does not
/// produce the active profile's vectors is refused, so vectors of different profiles never mix (doc 08, "WP6 detail"). A setting that names
/// a different provider is a pending switch (<see cref="ProfileSwitchPlanner"/>), not a mismatch: the active profile keeps serving.
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

        // The provider is the one the active profile was made with, not the one the setting names now: when the setting names another
        // provider, that is a planned switch (ProfileSwitchPlanner), and search keeps using the active profile until it is activated.
        if (!registry.TryGet(profile.ProviderId, out var provider))
        {
            throw new EmbeddingProfileMismatchException($"Provider '{profile.ProviderId}' of the active profile is not configured.");
        }

        if (!profile.Matches(provider.Descriptor))
        {
            throw new EmbeddingProfileMismatchException(
                $"Provider '{profile.ProviderId}' produces model '{provider.Descriptor.ModelId}' at {provider.Descriptor.Dimensions} dimensions; "
                + $"the active profile is '{profile.ModelId}' at {profile.Dimensions}. Changing profile is a planned re-embed: choose the new provider and let the indexer switch.");
        }

        return new ActiveEmbedding(profile, provider);
    }
}
