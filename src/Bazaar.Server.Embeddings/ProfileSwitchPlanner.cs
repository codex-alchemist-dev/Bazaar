namespace Bazaar.Server.Embeddings;

/// <summary>
/// Plans a profile switch (doc 08, "Planned profile switch" [PROPOSED]). When <c>memory.embedding_profile</c> names a provider whose profile
/// differs from the active one, the target is that provider's profile row (found, or created inactive). The active profile is never
/// changed here: the indexer backfills the target and activates it once coverage reaches the threshold.
/// </summary>
public sealed class ProfileSwitchPlanner(
    IEmbeddingSettingSource setting,
    IEmbeddingProfileStore profiles,
    IEmbeddingProviderRegistry registry)
{
    /// <summary>
    /// The target embedding to backfill, or null when nothing is pending: the setting is <c>none</c>, no profile is active yet (setup
    /// activates the first one), or the chosen provider already is the active profile. Throws <see cref="EmbeddingProfileMismatchException"/>
    /// when the chosen provider is not configured.
    /// </summary>
    public async Task<ActiveEmbedding?> ResolveTargetAsync(CancellationToken cancellationToken)
    {
        var chosen = await setting.GetEmbeddingProfileSettingAsync(cancellationToken).ConfigureAwait(false);
        if (EmbeddingSettings.IsNone(chosen))
        {
            return null;
        }

        var active = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return null;
        }

        if (!registry.TryGet(chosen, out var provider))
        {
            throw new EmbeddingProfileMismatchException($"Provider '{chosen}' is not configured.");
        }

        var descriptor = provider.Descriptor;
        if (string.Equals(active.ProviderId, chosen, StringComparison.Ordinal) && active.Matches(descriptor))
        {
            return null;
        }

        var target = await profiles.FindAsync(chosen, descriptor.ModelId, descriptor.Dimensions, cancellationToken).ConfigureAwait(false)
            ?? await profiles.CreateAsync(chosen, descriptor.ModelId, descriptor.Dimensions, cancellationToken).ConfigureAwait(false);
        return target.ProfileId == active.ProfileId ? null : new ActiveEmbedding(target, provider);
    }
}
