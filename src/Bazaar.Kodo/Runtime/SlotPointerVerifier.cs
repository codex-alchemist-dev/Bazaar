namespace Bazaar.Kodo.Runtime;

/// <summary>The outcome of verifying the pointer file against the manifest envelope it names.</summary>
public enum SlotPointerStatus
{
    /// <summary>The pointer is signed, names a valid slot, and the manifest it names is the one that was signed.</summary>
    Verified,

    /// <summary>No pointer bytes were given.</summary>
    PointerMissing,

    /// <summary>The pointer is not well formed (bad JSON, slot id or digest).</summary>
    Malformed,

    /// <summary>The pointer carries no signature.</summary>
    NoSignature,

    /// <summary>The signing key is not in the trust root.</summary>
    UnknownKey,

    /// <summary>The signing key is outside its validity window.</summary>
    KeyOutsideValidity,

    /// <summary>The signing key names an algorithm with no verifier.</summary>
    UnsupportedAlgorithm,

    /// <summary>The pointer signature does not match its body, so the pointer was changed after signing.</summary>
    BadSignature,

    /// <summary>No manifest envelope bytes were given for the slot the pointer names.</summary>
    ManifestMissing,

    /// <summary>The manifest envelope is not well formed, so it cannot be the one the pointer names.</summary>
    ManifestMalformed,

    /// <summary>The manifest envelope bytes do not hash to the digest the pointer names.</summary>
    ManifestDigestMismatch,

    /// <summary>The manifest names a different slot from the pointer.</summary>
    SlotIdMismatch,
}

/// <summary>
/// Verifies a slot pointer and that it names the manifest envelope on disk (doc 46 section 6.3). Pure: the caller reads the bytes.
/// The slot's files are checked separately by <see cref="RuntimeManifestVerifier"/>. A pointer that fails here is never followed.
/// </summary>
public sealed class SlotPointerVerifier
{
    private readonly RuntimeSignatureChecker _signatures;

    /// <summary>Creates a verifier that checks signatures with <paramref name="signatures"/>.</summary>
    public SlotPointerVerifier(RuntimeSignatureChecker signatures)
    {
        _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
    }

    /// <summary>
    /// Verifies the pointer bytes (null when the file is missing), then the manifest envelope bytes (null when missing) against the digest
    /// and slot id the pointer names.
    /// </summary>
    public SlotPointerStatus Verify(byte[]? pointerFile, byte[]? manifestEnvelope, DateTimeOffset now)
    {
        if (pointerFile is null)
        {
            return SlotPointerStatus.PointerMissing;
        }

        SlotPointer? pointer = RuntimeManifestCodec.ParsePointer(pointerFile);
        if (pointer is null
            || !RuntimeLayout.IsSlotId(pointer.SlotId)
            || !RuntimeManifestRules.IsSha256Hex(pointer.ManifestSha256))
        {
            return SlotPointerStatus.Malformed;
        }

        RuntimeSignatureStatus signature = _signatures.Check(
            RuntimeManifestCodec.CanonicalizePointerBody(pointer.ToBody()),
            pointer.KeyId,
            pointer.Signature,
            now);
        if (signature != RuntimeSignatureStatus.Valid)
        {
            return ToPointerStatus(signature);
        }

        if (manifestEnvelope is null)
        {
            return SlotPointerStatus.ManifestMissing;
        }

        ReadOnlySpan<byte> envelope = manifestEnvelope;
        if (!string.Equals(RuntimeManifestCodec.Sha256Hex(envelope), pointer.ManifestSha256, StringComparison.Ordinal))
        {
            return SlotPointerStatus.ManifestDigestMismatch;
        }

        SignedRuntimeManifest? signed = RuntimeManifestCodec.ParseEnvelope(envelope);
        if (signed?.Manifest is null)
        {
            return SlotPointerStatus.ManifestMalformed;
        }

        return string.Equals(signed.Manifest.SlotId, pointer.SlotId, StringComparison.Ordinal)
            ? SlotPointerStatus.Verified
            : SlotPointerStatus.SlotIdMismatch;
    }

    private static SlotPointerStatus ToPointerStatus(RuntimeSignatureStatus signature) => signature switch
    {
        RuntimeSignatureStatus.NoSignature => SlotPointerStatus.NoSignature,
        RuntimeSignatureStatus.UnknownKey => SlotPointerStatus.UnknownKey,
        RuntimeSignatureStatus.KeyOutsideValidity => SlotPointerStatus.KeyOutsideValidity,
        RuntimeSignatureStatus.UnsupportedAlgorithm => SlotPointerStatus.UnsupportedAlgorithm,
        _ => SlotPointerStatus.BadSignature,
    };
}
