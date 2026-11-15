using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace Bazaar.Conduit.PeerToPeer;

/// <summary>A STUN Binding Request ready to send, with the transaction id that its response must echo.</summary>
/// <param name="Packet">Wire bytes: a 20-byte header and no attributes.</param>
/// <param name="TransactionId">The 96-bit transaction id.</param>
public sealed record StunBindingRequest(byte[] Packet, byte[] TransactionId);

/// <summary>
/// STUN (RFC 5389) Binding codec. Builds Binding Requests and reads the mapped address from Binding Success Responses.
/// Parsing never throws: malformed or mismatched input returns null.
/// </summary>
public static class StunMessage
{
    /// <summary>Length of the fixed STUN header.</summary>
    public const int HeaderLength = 20;

    /// <summary>Length of the transaction id.</summary>
    public const int TransactionIdLength = 12;

    /// <summary>Fixed magic cookie (RFC 5389 section 6).</summary>
    public const uint MagicCookie = 0x2112A442;

    /// <summary>Binding Request message type.</summary>
    public const ushort BindingRequestType = 0x0001;

    /// <summary>Binding Success Response message type.</summary>
    public const ushort BindingSuccessResponseType = 0x0101;

    private const ushort _mappedAddressAttribute = 0x0001;
    private const ushort _xorMappedAddressAttribute = 0x0020;
    private const byte _familyIPv4 = 0x01;
    private const byte _familyIPv6 = 0x02;

    /// <summary>Creates a Binding Request with a random transaction id.</summary>
    public static StunBindingRequest CreateBindingRequest() =>
        CreateBindingRequest(RandomNumberGenerator.GetBytes(TransactionIdLength));

    /// <summary>Creates a Binding Request with the given 12-byte transaction id.</summary>
    /// <exception cref="ArgumentException">The transaction id is not 12 bytes.</exception>
    public static StunBindingRequest CreateBindingRequest(ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != TransactionIdLength)
        {
            throw new ArgumentException($"Transaction id must be {TransactionIdLength} bytes.", nameof(transactionId));
        }

        var packet = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), BindingRequestType);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
        transactionId.CopyTo(packet.AsSpan(8));
        return new StunBindingRequest(packet, transactionId.ToArray());
    }

    /// <summary>
    /// Reads the reflexive address from a Binding Success Response. XOR-MAPPED-ADDRESS is preferred; MAPPED-ADDRESS is
    /// used when it is the only address attribute. Returns null when the message is not a success response for this
    /// transaction, is malformed, or carries no usable address.
    /// </summary>
    public static IPEndPoint? ReadMappedAddress(ReadOnlySpan<byte> response, ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != TransactionIdLength || response.Length < HeaderLength)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(response) != BindingSuccessResponseType)
        {
            return null;
        }

        var bodyLength = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if (BinaryPrimitives.ReadUInt32BigEndian(response[4..]) != MagicCookie)
        {
            return null;
        }

        if (bodyLength % 4 != 0 || HeaderLength + bodyLength > response.Length)
        {
            return null;
        }

        if (!response.Slice(8, TransactionIdLength).SequenceEqual(transactionId))
        {
            return null;
        }

        var end = HeaderLength + bodyLength;
        IPEndPoint? mapped = null;
        var offset = HeaderLength;
        while (offset + 4 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);
            var valueStart = offset + 4;
            if (valueStart + length > end)
            {
                return null;
            }

            var value = response.Slice(valueStart, length);
            if (type == _xorMappedAddressAttribute)
            {
                var xor = DecodeAddress(value, transactionId, xor: true);
                if (xor is not null)
                {
                    return xor;
                }
            }
            else if (type == _mappedAddressAttribute && mapped is null)
            {
                mapped = DecodeAddress(value, transactionId, xor: false);
            }

            // Attribute values are padded to a multiple of four bytes; unknown attributes are skipped the same way.
            offset = valueStart + ((length + 3) & ~3);
        }

        return mapped;
    }

    private static IPEndPoint? DecodeAddress(ReadOnlySpan<byte> value, ReadOnlySpan<byte> transactionId, bool xor)
    {
        if (value.Length < 4)
        {
            return null;
        }

        var family = value[1];
        var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);

        // XOR key: magic cookie, then transaction id (RFC 5389 section 15.2). IPv4 uses only the first four bytes.
        Span<byte> key = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(key, MagicCookie);
        transactionId.CopyTo(key[4..]);

        if (family == _familyIPv4)
        {
            if (value.Length != 8)
            {
                return null;
            }

            var address = value.Slice(4, 4).ToArray();
            if (xor)
            {
                port ^= (ushort)(MagicCookie >> 16);
                XorInPlace(address, key[..4]);
            }

            return port == 0 ? null : new IPEndPoint(new IPAddress(address), port);
        }

        if (family == _familyIPv6)
        {
            if (value.Length != 20)
            {
                return null;
            }

            var address = value.Slice(4, 16).ToArray();
            if (xor)
            {
                port ^= (ushort)(MagicCookie >> 16);
                XorInPlace(address, key);
            }

            return port == 0 ? null : new IPEndPoint(new IPAddress(address), port);
        }

        return null;
    }

    private static void XorInPlace(byte[] target, ReadOnlySpan<byte> key)
    {
        for (var i = 0; i < target.Length; i++)
        {
            target[i] ^= key[i];
        }
    }
}
