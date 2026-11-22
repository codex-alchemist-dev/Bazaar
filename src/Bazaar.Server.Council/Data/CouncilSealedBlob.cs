using System.Security.Cryptography;

namespace Bazaar.Server.Council.Data;

/// <summary>
/// The self-describing blob a <c>council.reason</c> event stores in <c>gc_event.payload_ct</c> (doc 10, Envelope): one format
/// byte, the 12-byte nonce, then the ciphertext with its tag. The key id travels in <c>gc_event.key_id</c>. This only repacks the
/// bytes the <see cref="ICouncilReasoningCipher"/> already produced; it never decrypts and holds no key.
/// </summary>
public static class CouncilSealedBlob
{
    /// <summary>Format byte of the version 1 layout.</summary>
    public const byte FormatV1 = 1;

    /// <summary>Nonce length the layout requires.</summary>
    public const int NonceLength = 12;

    /// <summary>Packs a stored seal into the blob.</summary>
    public static byte[] Pack(ReasoningCiphertext sealedText)
    {
        ArgumentNullException.ThrowIfNull(sealedText);
        if (sealedText.Nonce.Length != NonceLength)
        {
            throw new CryptographicException($"The sealed reasoning nonce must be {NonceLength} bytes.");
        }

        if (sealedText.Ciphertext.Length == 0)
        {
            throw new CryptographicException("The sealed reasoning is empty.");
        }

        var blob = new byte[1 + NonceLength + sealedText.Ciphertext.Length];
        blob[0] = FormatV1;
        sealedText.Nonce.CopyTo(blob, 1);
        sealedText.Ciphertext.CopyTo(blob, 1 + NonceLength);
        return blob;
    }

    /// <summary>Unpacks a blob and its key id into the stored seal. Throws on an unknown format byte or a short blob.</summary>
    public static ReasoningCiphertext Unpack(ReadOnlySpan<byte> blob, Guid keyId)
    {
        if (blob.Length <= 1 + NonceLength || blob[0] != FormatV1)
        {
            throw new CryptographicException("The sealed council reason has an unknown format or is too short.");
        }

        return new ReasoningCiphertext(keyId, blob[(1 + NonceLength)..].ToArray(), blob.Slice(1, NonceLength).ToArray());
    }
}
