namespace Bazaar.Server.WebAdmin.Http;

/// <summary>Source of the panel's static files. <paramref name="path"/> is relative to the panel root, such as <c>index.html</c> or <c>assets/panel.js</c>.</summary>
public interface IAdminAssetProvider
{
    bool TryGet(string path, out byte[] content, out string contentType);
}
