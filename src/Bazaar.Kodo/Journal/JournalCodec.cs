using System.Buffers.Binary;
using System.Text;

namespace Bazaar.Kodo.Journal;

/// <summary>
/// Canonical byte encoding of a journal record's own fields (everything except the two hashes).
/// Layout, integers big-endian: version (1 byte), sequence (int64), UTC ticks (int64),
/// kind length (uint16) and UTF-8 kind, payload length (uint32) and payload bytes.
/// The encoding is fixed-width and length-prefixed, so no canonical-JSON step is needed.
/// </summary>
public static class JournalCodec
{
    /// <summary>Format version, the first byte of every canonical encoding.</summary>
    public const byte Version = 1;

    private const int SequenceOffset = 1;
    private const int TicksOffset = 9;
    private const int KindLengthOffset = 17;
    private const int KindOffset = 19;

    /// <summary>Encodes the fields of <paramref name="record"/>. The two hash fields are ignored.</summary>
    /// <exception cref="ArgumentException">The kind is empty or longer than 65535 UTF-8 bytes.</exception>
    public static byte[] Encode(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        byte[] kind = Encoding.UTF8.GetBytes(record.Kind);
        if (kind.Length is 0 or > ushort.MaxValue)
        {
            throw new ArgumentException("Kind must be 1 to 65535 UTF-8 bytes.", nameof(record));
        }

        int payloadLengthOffset = KindOffset + kind.Length;
        byte[] result = new byte[payloadLengthOffset + sizeof(uint) + record.Payload.Length];
        Span<byte> span = result;

        span[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(span[SequenceOffset..], record.Sequence);
        BinaryPrimitives.WriteInt64BigEndian(span[TicksOffset..], record.At.UtcTicks);
        BinaryPrimitives.WriteUInt16BigEndian(span[KindLengthOffset..], (ushort)kind.Length);
        kind.CopyTo(span[KindOffset..]);
        BinaryPrimitives.WriteUInt32BigEndian(span[payloadLengthOffset..], (uint)record.Payload.Length);
        record.Payload.CopyTo(span[(payloadLengthOffset + sizeof(uint))..]);

        return result;
    }

    /// <summary>Decodes canonical bytes and attaches the given hashes. Rejects any length or version mismatch.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed canonical record.</exception>
    public static JournalRecord Decode(ReadOnlySpan<byte> canonical, byte[] previousHash, byte[] hash)
    {
        if (canonical.Length < KindOffset + sizeof(uint))
        {
            throw new InvalidDataException("Canonical journal record is truncated.");
        }

        if (canonical[0] != Version)
        {
            throw new InvalidDataException($"Unknown journal record version {canonical[0]}.");
        }

        long sequence = BinaryPrimitives.ReadInt64BigEndian(canonical[SequenceOffset..]);
        long ticks = BinaryPrimitives.ReadInt64BigEndian(canonical[TicksOffset..]);
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException("Journal record timestamp is out of range.");
        }

        int kindLength = BinaryPrimitives.ReadUInt16BigEndian(canonical[KindLengthOffset..]);
        int payloadLengthOffset = KindOffset + kindLength;
        if (canonical.Length < payloadLengthOffset + sizeof(uint))
        {
            throw new InvalidDataException("Canonical journal record is truncated.");
        }

        string kind = Encoding.UTF8.GetString(canonical.Slice(KindOffset, kindLength));
        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(canonical[payloadLengthOffset..]);
        int payloadOffset = payloadLengthOffset + sizeof(uint);
        if ((uint)(canonical.Length - payloadOffset) != payloadLength)
        {
            throw new InvalidDataException("Canonical journal record length does not match its header.");
        }

        return new JournalRecord
        {
            Sequence = sequence,
            At = new DateTimeOffset(ticks, TimeSpan.Zero),
            Kind = kind,
            Payload = canonical[payloadOffset..].ToArray(),
            PreviousHash = previousHash,
            Hash = hash,
        };
    }
}
