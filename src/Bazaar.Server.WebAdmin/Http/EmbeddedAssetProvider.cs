using Bazaar.Server.WebAdmin.Assets;

namespace Bazaar.Server.WebAdmin.Http;

/// <summary>Serves the assets embedded in this assembly (<see cref="AdminAssets"/>).</summary>
public sealed class EmbeddedAssetProvider : IAdminAssetProvider
{
    public bool TryGet(string path, out byte[] content, out string contentType) => AdminAssets.TryGet(path, out content, out contentType);
}
