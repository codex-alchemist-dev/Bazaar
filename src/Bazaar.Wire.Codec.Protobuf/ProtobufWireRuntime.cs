using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Codec.Protobuf.Proto;
using Google.Protobuf;

namespace Bazaar.Wire.Codec.Protobuf;

/// <summary>
/// Helpers the generated mappers call. Every decode limit lives here, so a generated mapper cannot forget one. Depth counts
/// the envelope as level 1, the payload record as level 2, and every nested record or list as one more level, the same way the
/// CBOR codec counts, so one <see cref="WireDecodeLimits"/> means the same thing in every codec.
/// </summary>
internal static class ProtobufWireRuntime
{
    /// <summary>The payload written for a message with no set fields: field 536870911 (the largest number), varint 0. Readers skip it.</summary>
    private static readonly byte[] _emptyPayload = [0xF8, 0xFF, 0xFF, 0xFF, 0x0F, 0x00];

    /// <summary>A fresh copy of the marker payload for an empty message.</summary>
    public static byte[] EmptyPayload() => (byte[])_emptyPayload.Clone();

    /// <summary>A reader over <paramref name="data"/> with the recursion cap set from the decode limits.</summary>
    public static CodedInputStream NewInput(ReadOnlyMemory<byte> data, ProtobufLimits l)
    {
        ArraySegment<byte> segment;
        if (!MemoryMarshal.TryGetArray(data, out segment)) segment = new ArraySegment<byte>(data.ToArray());
        var stream = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
        // The envelope is level 1, so a payload may nest MaxDepth - 1 messages or groups.
        return CodedInputStream.CreateWithLimits(stream, Math.Max(data.Length, 1), Math.Max(l.MaxDepth - 1, 1));
    }

    public static void CheckDepth(int depth, ProtobufLimits l)
    {
        if (depth > l.MaxDepth) throw new WireFormatException("Data nests deeper than the decode limit.");
    }

    public static void CheckList(int count, ProtobufLimits l, int depth)
    {
        if (count > l.MaxItems) throw new WireFormatException("A list is longer than the decode limit.");
        if (count > 0 && depth > l.MaxDepth) throw new WireFormatException("Data nests deeper than the decode limit.");
    }

    public static T Require<T>(T? value, string field)
        where T : class =>
        value ?? throw new WireFormatException($"Payload has no '{field}'.");

    public static string Text(string text, ProtobufLimits l)
    {
        // Characters never outnumber bytes, so the byte count is only worth computing for long strings.
        if (text.Length > l.MaxStringBytes / 3 && Encoding.UTF8.GetByteCount(text) > l.MaxStringBytes)
        {
            throw new WireFormatException("A string is longer than the decode limit.");
        }

        return text;
    }

    public static byte Byte(uint value) =>
        value <= byte.MaxValue ? (byte)value : throw new WireFormatException("A byte value is out of range.");

    public static string DecimalText(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

    public static decimal Decimal(string text, ProtobufLimits l)
    {
        if (text.Length > 64) throw new WireFormatException("A decimal is too long.");
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw new WireFormatException("A decimal is malformed.");
        return value;
    }

    public static ByteString GuidBytes(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return ByteString.CopyFrom(bytes);
    }

    public static Guid Guid(ByteString bytes)
    {
        if (bytes.Length != 16) throw new WireFormatException("A GUID is not 16 bytes.");
        return new Guid(bytes.Span, bigEndian: true);
    }

    public static Instant ToInstant(DateTimeOffset value) => new()
    {
        UtcTicks = value.UtcTicks,
        OffsetMinutes = (int)value.Offset.TotalMinutes,
    };

    public static DateTimeOffset FromInstant(Instant value)
    {
        if (value.OffsetMinutes is < -14 * 60 or > 14 * 60) throw new WireFormatException("A time zone offset is out of range.");
        var offset = TimeSpan.FromMinutes(value.OffsetMinutes);
        if (value.UtcTicks < DateTimeOffset.MinValue.UtcTicks || value.UtcTicks > DateTimeOffset.MaxValue.UtcTicks) throw new WireFormatException("A time is out of range.");
        var local = value.UtcTicks + offset.Ticks;
        if (local < DateTime.MinValue.Ticks || local > DateTime.MaxValue.Ticks) throw new WireFormatException("A time is out of range.");
        return new DateTimeOffset(local, offset);
    }

    public static WireFormatException UnknownEnum(string type, int value) => new($"{value} is not a known {type}.");
}
