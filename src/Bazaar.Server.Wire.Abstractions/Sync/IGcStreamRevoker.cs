namespace Bazaar.Server.Wire.Sync;

/// <summary>
/// Re-checks the live subscribers of a GC stream after its membership changed (doc 02, <c>stream.revoked</c>). The GC component
/// calls it; the sync engine implements it, so neither needs the other's project.
/// </summary>
public interface IGcStreamRevoker
{
    /// <summary>Re-checks every subscriber's access; those no longer allowed get <c>stream.revoked</c>.</summary>
    Task RecheckAccessAsync(Guid gcId, IGcAccessPolicy access, CancellationToken ct);
}
