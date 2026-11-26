using Bazaar.Server.Embeddings;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Host.Components.Embeddings;

/// <summary>
/// Reads <c>memory.embedding_profile</c> from the host's setting store. It lives in the host because <see cref="ISettingStore"/> is
/// in <c>Bazaar.Server</c>, which modules never reference.
/// </summary>
internal sealed class SettingStoreEmbeddingSettingSource(ISettingStore store) : IEmbeddingSettingSource
{
    public Task<string> GetEmbeddingProfileSettingAsync(CancellationToken cancellationToken) =>
        store.GetAsync(EmbeddingSettings.EmbeddingProfileKey, cancellationToken: cancellationToken);
}
