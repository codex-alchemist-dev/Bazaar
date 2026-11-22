namespace Bazaar.Server.Providers;

/// <summary>
/// Marker for the VM role. The VM contract (<c>IVmBackend</c>) is doc 16's and is added with WP13;
/// this role exists so the selector can choose a backend per OS.
/// </summary>
public interface IVmProvider : IProvider
{
}
