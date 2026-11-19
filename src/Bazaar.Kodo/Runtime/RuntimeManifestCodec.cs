using System.Security.Cryptography;
using System.Text.Json;

namespace Bazaar.Kodo.Runtime;

/// <summary>
/// Canonical JSON for the signed runtime records, and parsing of the envelopes that carry them. The canonical bytes of a
/// manifest or a pointer body are the signed message. This is a Bazaar profile of canonical JSON (fixed property order,
/// no whitespace), not RFC 8785 (doc 46 section 6.1).
/// </summary>
public static class RuntimeManifestCodec
{
    /// <summary>The canonical UTF-8 JSON of a manifest, which is the message the release key signs.</summary>
    public static byte[] CanonicalizeManifest(RuntimeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest, RuntimeJsonContext.Default.RuntimeManifest);
    }

    /// <summary>The canonical UTF-8 JSON of a pointer body, which is the message the release key signs.</summary>
    public static byte[] CanonicalizePointerBody(SlotPointerBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return JsonSerializer.SerializeToUtf8Bytes(body, RuntimeJsonContext.Default.SlotPointerBody);
    }

    /// <summary>The canonical UTF-8 JSON of a pointer body. Same bytes as <see cref="CanonicalizePointerBody(SlotPointerBody)"/>.</summary>
    public static byte[] CanonicalizePointer(SlotPointer current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return CanonicalizePointerBody(current.ToBody());
    }

    /// <summary>Writes a signed manifest envelope as stored on disk.</summary>
    public static byte[] EncodeEnvelope(SignedRuntimeManifest envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, RuntimeJsonContext.Default.SignedRuntimeManifest);
    }

    /// <summary>Parses a signed manifest envelope. Returns null when the bytes are not a well-formed envelope.</summary>
    public static SignedRuntimeManifest? ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, RuntimeJsonContext.Default.SignedRuntimeManifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes a slot pointer file as stored on disk.</summary>
    public static byte[] EncodePointer(SlotPointer current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return JsonSerializer.SerializeToUtf8Bytes(current, RuntimeJsonContext.Default.SlotPointer);
    }

    /// <summary>Parses a slot pointer file. Returns null when the bytes are not a well-formed pointer.</summary>
    public static SlotPointer? ParsePointer(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, RuntimeJsonContext.Default.SlotPointer);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 of the given bytes as 64 lowercase hex characters.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
