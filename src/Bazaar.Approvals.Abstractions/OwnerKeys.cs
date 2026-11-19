namespace Bazaar.Approvals;

/// <summary>A device key the owner registered for signing decisions. Revoked keys are never accepted.</summary>
/// <param name="OwnerId">Owner the key belongs to.</param>
/// <param name="DeviceId">Device key id, limited to <c>^[A-Za-z0-9._-]{1,128}\z</c>.</param>
/// <param name="PublicKeySpki">The P-256 public key, DER SubjectPublicKeyInfo.</param>
/// <param name="Revoked">True once the owner revoked the device.</param>
public sealed record OwnerDeviceKey(string OwnerId, string DeviceId, byte[] PublicKeySpki, bool Revoked = false);

/// <summary>Finds the owner's device keys. Backed by the Identity Service in production (doc 14); in memory in tests.</summary>
public interface IOwnerKeyRegistry
{
    /// <summary>The key for a device id, or null when none is registered.</summary>
    Task<OwnerDeviceKey?> FindAsync(string deviceId, CancellationToken ct = default);
}
