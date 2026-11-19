using System.Buffers.Binary;
using System.Text;

namespace Bazaar.Server.VmEgress.Proxy;

/// <summary>Outcome of looking at the first bytes a client sent into a CONNECT tunnel.</summary>
internal enum TlsHelloStatus
{
    /// <summary>The bytes do not begin a TLS record (content type 0x16, version major 3). Relay them unchanged.</summary>
    NotTls,

    /// <summary>A TLS handshake has started but the ClientHello is not complete yet. Read more and call again.</summary>
    NeedMore,

    /// <summary>A complete ClientHello with exactly one host_name server_name entry.</summary>
    Ok,

    /// <summary>It began as TLS but is not a well-formed ClientHello. Refuse.</summary>
    Malformed,

    /// <summary>A well-formed ClientHello without a server_name extension. Refuse.</summary>
    MissingSni,

    /// <summary>The ClientHello carries an encrypted_client_hello (ECH) or draft ESNI extension, which hides the real name. Refuse.</summary>
    Ech,
}

/// <summary>The result of <see cref="TlsClientHello.Parse"/>. <see cref="ServerName"/> is set only for <see cref="TlsHelloStatus.Ok"/>.</summary>
internal readonly record struct TlsHelloResult(TlsHelloStatus Status, string? ServerName = null);

/// <summary>
/// A strict, allocation-light reader for the ClientHello server name, used to pin the SNI of a CONNECT tunnel to the name the
/// allowlist approved (domain fronting defence). Pure: it never throws, never reads past its input and keeps no state. A
/// ClientHello split over several handshake records is reassembled up to <see cref="MaxBytes"/> of wire data.
/// An SSLv2-style hello (first byte 0x80) is not a TLS record in this sense and is reported as <see cref="TlsHelloStatus.NotTls"/>;
/// the proxy then relays it unchanged like any other non-TLS protocol, which is accepted because no modern server speaks it.
/// </summary>
internal static class TlsClientHello
{
    /// <summary>Most wire bytes (record headers included) examined before a hello that is still incomplete is refused.</summary>
    public const int MaxBytes = 16 * 1024;

    private const byte _handshakeRecord = 0x16;
    private const byte _clientHelloType = 1;
    private const int _maxRecordPayload = 16 * 1024;
    private const ushort _serverNameExtension = 0;
    private const ushort _echExtension = 0xfe0d;
    private const ushort _esniDraftExtension = 0xffce;

    /// <summary>Examines <paramref name="data"/>, the first bytes the client sent after the tunnel opened.</summary>
    public static TlsHelloResult Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            return ParseCore(data);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            // Not reachable by construction (every read is bounds-checked); this keeps the "never throws" promise if it ever is.
            return new(TlsHelloStatus.Malformed);
        }
    }

    private static TlsHelloResult ParseCore(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return new(TlsHelloStatus.NeedMore);
        }

        // Another TLS record type (change_cipher_spec, alert, application_data, heartbeat) ahead of the ClientHello is refused:
        // some TLS stacks skip a warning alert before the hello, which would carry an unchecked hello past this check.
        var otherTlsRecord = data[0] is >= 0x14 and <= 0x18 and not _handshakeRecord;
        if (data[0] != _handshakeRecord && !otherTlsRecord)
        {
            return new(TlsHelloStatus.NotTls);
        }

        if (data.Length < 2)
        {
            return new(TlsHelloStatus.NeedMore);
        }

        if (otherTlsRecord)
        {
            return new(data[1] == 3 ? TlsHelloStatus.Malformed : TlsHelloStatus.NotTls);
        }

        if (data[1] != 3)
        {
            return new(TlsHelloStatus.NotTls);
        }

        var handshake = new List<byte>(Math.Min(data.Length, MaxBytes));
        var offset = 0;
        while (true)
        {
            // Look at the handshake bytes gathered so far; this decides as soon as the hello is complete or provably bad.
            if (handshake.Count >= 1 && handshake[0] != _clientHelloType)
            {
                return new(TlsHelloStatus.Malformed);
            }

            if (handshake.Count >= 4)
            {
                var length = (handshake[1] << 16) | (handshake[2] << 8) | handshake[3];
                if (4 + length > MaxBytes)
                {
                    return new(TlsHelloStatus.Malformed);
                }

                if (handshake.Count > 4 + length)
                {
                    // Anything after the ClientHello in the same flight is not valid client behaviour at this point.
                    return new(TlsHelloStatus.Malformed);
                }

                if (handshake.Count == 4 + length)
                {
                    return ParseBody(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(handshake)[4..]);
                }
            }

            // Need another record fragment.
            var remaining = data[offset..];
            if (remaining.IsEmpty)
            {
                return NeedMoreOrTooBig(data.Length);
            }

            if (remaining[0] != _handshakeRecord)
            {
                return new(TlsHelloStatus.Malformed);
            }

            if (remaining.Length >= 2 && remaining[1] != 3)
            {
                return new(TlsHelloStatus.Malformed);
            }

            if (remaining.Length < 5)
            {
                return NeedMoreOrTooBig(data.Length);
            }

            var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(remaining[3..5]);
            if (payloadLength is 0 || payloadLength > _maxRecordPayload)
            {
                return new(TlsHelloStatus.Malformed);
            }

            if (offset + 5 + payloadLength > MaxBytes)
            {
                return new(TlsHelloStatus.Malformed);
            }

            if (remaining.Length < 5 + payloadLength)
            {
                // Check the part of the fragment already here so an obviously wrong message type is refused without waiting.
                if (handshake.Count == 0 && remaining.Length > 5 && remaining[5] != _clientHelloType)
                {
                    return new(TlsHelloStatus.Malformed);
                }

                return NeedMoreOrTooBig(data.Length);
            }

            foreach (var b in remaining.Slice(5, payloadLength))
            {
                handshake.Add(b);
            }

            offset += 5 + payloadLength;
        }
    }

    private static TlsHelloResult NeedMoreOrTooBig(int have) =>
        new(have >= MaxBytes ? TlsHelloStatus.Malformed : TlsHelloStatus.NeedMore);

    private static TlsHelloResult ParseBody(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body);

        // legacy_version, random.
        if (!r.Skip(2) || !r.Skip(32))
        {
            return new(TlsHelloStatus.Malformed);
        }

        if (!r.TryReadU8(out var sessionIdLength) || sessionIdLength > 32 || !r.Skip(sessionIdLength))
        {
            return new(TlsHelloStatus.Malformed);
        }

        if (!r.TryReadU16(out var suitesLength) || suitesLength < 2 || (suitesLength & 1) != 0 || !r.Skip(suitesLength))
        {
            return new(TlsHelloStatus.Malformed);
        }

        if (!r.TryReadU8(out var compressionLength) || compressionLength < 1 || !r.Skip(compressionLength))
        {
            return new(TlsHelloStatus.Malformed);
        }

        if (r.Remaining == 0)
        {
            // A hello without extensions has no server name.
            return new(TlsHelloStatus.MissingSni);
        }

        if (!r.TryReadU16(out var extensionsLength) || extensionsLength != r.Remaining)
        {
            return new(TlsHelloStatus.Malformed);
        }

        string? name = null;
        var sawServerName = false;
        var sawEch = false;
        var seen = new HashSet<int>();
        while (r.Remaining > 0)
        {
            if (!r.TryReadU16(out var type) || !r.TryReadU16(out var length) || !r.TryRead(length, out var ext) || !seen.Add(type))
            {
                return new(TlsHelloStatus.Malformed);
            }

            if (type is _echExtension or _esniDraftExtension)
            {
                sawEch = true;
            }
            else if (type == _serverNameExtension)
            {
                sawServerName = true;
                var parsed = ParseServerName(ext, out name);
                if (parsed is not TlsHelloStatus.Ok)
                {
                    return new(parsed);
                }
            }
        }

        if (sawEch)
        {
            return new(TlsHelloStatus.Ech);
        }

        return sawServerName ? new(TlsHelloStatus.Ok, name) : new(TlsHelloStatus.MissingSni);
    }

    private static TlsHelloStatus ParseServerName(ReadOnlySpan<byte> ext, out string? name)
    {
        name = null;
        var r = new Reader(ext);
        if (!r.TryReadU16(out var listLength) || listLength != r.Remaining || listLength == 0)
        {
            return TlsHelloStatus.Malformed;
        }

        if (!r.TryReadU8(out var nameType) || nameType != 0 || !r.TryReadU16(out var nameLength) || nameLength == 0 || nameLength > 253
            || !r.TryRead(nameLength, out var raw))
        {
            return TlsHelloStatus.Malformed;
        }

        if (r.Remaining != 0)
        {
            // A second entry. RFC 6066 allows one per name type, and only host_name is defined, so two is never legitimate.
            return TlsHelloStatus.Malformed;
        }

        foreach (var b in raw)
        {
            if (b is <= 0x20 or >= 0x7f)
            {
                // host_name is ASCII (A-labels for IDN). Controls, spaces and non-ASCII bytes are refused.
                return TlsHelloStatus.Malformed;
            }
        }

        name = Encoding.ASCII.GetString(raw);
        return TlsHelloStatus.Ok;
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public readonly int Remaining => _data.Length - _pos;

        public bool Skip(int count)
        {
            if (count < 0 || count > Remaining)
            {
                return false;
            }

            _pos += count;
            return true;
        }

        public bool TryReadU8(out int value)
        {
            value = 0;
            if (Remaining < 1)
            {
                return false;
            }

            value = _data[_pos++];
            return true;
        }

        public bool TryReadU16(out int value)
        {
            value = 0;
            if (Remaining < 2)
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt16BigEndian(_data[_pos..]);
            _pos += 2;
            return true;
        }

        public bool TryRead(int count, out ReadOnlySpan<byte> slice)
        {
            slice = default;
            if (count < 0 || count > Remaining)
            {
                return false;
            }

            slice = _data.Slice(_pos, count);
            _pos += count;
            return true;
        }
    }
}
