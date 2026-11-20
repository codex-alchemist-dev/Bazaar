namespace Bazaar.Kodo.Runtime;

/// <summary>What the pointer signature covers: the slot it selects and the digest of that slot's signed manifest envelope.</summary>
public sealed record SlotPointerBody(string SlotId, string ManifestSha256);

/// <summary>
/// The pointer file (doc 46 section 6.3): which slot is active, the SHA-256 (lowercase hex) of that slot's signed manifest
/// envelope, the signing key id, and the base64 signature over the canonical <see cref="SlotPointerBody"/>.
/// </summary>
public sealed record SlotPointer(string SlotId, string ManifestSha256, string? KeyId, string? Signature)
{
    /// <summary>The part of the pointer that the signature covers. A method, not a property, so the serializer never writes it.</summary>
    public SlotPointerBody ToBody() => new(SlotId, ManifestSha256);
}
