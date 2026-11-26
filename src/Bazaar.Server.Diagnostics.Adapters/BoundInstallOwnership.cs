using Bazaar.Diagnostics.Ingestion;

namespace Bazaar.Server.Diagnostics.Adapters;

/// <summary>
/// Install ownership decided by the install registry for one connection: the install is owned when the registry bound it to this
/// device. The crash handlers build their intake service over it.
/// </summary>
internal sealed class BoundInstallOwnership(Guid installId, bool bound) : IInstallOwnershipResolver
{
    /// <inheritdoc />
    public bool OwnsInstall(Guid candidate) => bound && candidate == installId;
}
