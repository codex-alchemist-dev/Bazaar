using System.Formats.Asn1;
using System.Security.Cryptography;

namespace Bazaar.Client.Approvals;

/// <summary>
/// Converts ECDSA P-256 signatures between the DER form the Android Keystore returns and the 64-byte IEEE P1363 form the
/// approval payload and the server expect (<see cref="IDeviceApprovalSigner"/>).
/// </summary>
public static class EcdsaSignatureFormat
{
    private const int _p256Size = 32;

    /// <summary>Converts a DER (ASN.1 SEQUENCE of two INTEGERs) signature to 64-byte P1363. Throws <see cref="CryptographicException"/> for anything else.</summary>
    public static byte[] DerToP1363(ReadOnlySpan<byte> der)
    {
        try
        {
            var reader = new AsnReader(der.ToArray(), AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            var r = sequence.ReadIntegerBytes().Span;
            var s = sequence.ReadIntegerBytes().Span;
            sequence.ThrowIfNotEmpty();

            var result = new byte[_p256Size * 2];
            Place(r, result.AsSpan(0, _p256Size));
            Place(s, result.AsSpan(_p256Size, _p256Size));
            return result;
        }
        catch (AsnContentException ex)
        {
            throw new CryptographicException("The signature is not valid DER.", ex);
        }
    }

    private static void Place(ReadOnlySpan<byte> value, Span<byte> destination)
    {
        // A DER integer is signed big-endian: drop the sign byte, then left-pad to the field size.
        while (value.Length > 1 && value[0] == 0)
        {
            value = value[1..];
        }

        if (value.Length > destination.Length)
        {
            throw new CryptographicException("The signature value is too long for P-256.");
        }

        value.CopyTo(destination[(destination.Length - value.Length)..]);
    }
}
