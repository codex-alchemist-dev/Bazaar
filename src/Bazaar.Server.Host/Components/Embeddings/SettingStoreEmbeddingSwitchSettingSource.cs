using Bazaar.Server.Embeddings;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Host.Components.Embeddings;

/// <summary>
/// Reads <c>memory.embedding_switch_threshold</c> from the host's setting store. The key's default (1.0) answers when nothing is stored.
/// </summary>
internal sealed class SettingStoreEmbeddingSwitchSettingSource(ISettingStore store) : IEmbeddingSwitchSettingSource
{
    public Task<decimal> GetSwitchThresholdAsync(CancellationToken cancellationToken) =>
        store.GetAsync(EmbeddingSettings.SwitchThresholdKey, cancellationToken: cancellationToken);
}
