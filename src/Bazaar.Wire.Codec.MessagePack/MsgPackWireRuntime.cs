using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Bazaar.Contracts.Wire.Codec;
using global::MessagePack;

namespace Bazaar.Wire.Codec.MessagePack;

/// <summary>
/// Helpers the generated MessagePack readers and writers call. Every decode limit lives here, so a generated reader cannot forget
/// one: depth, string length and collection length are checked as each container or string is read (doc 90 section 4.5).
/// Only the low-level <see cref="MessagePackReader"/> and <see cref="MessagePackWriter"/> are used; there is no resolver, no
/// type-name lookup and no reflection. Containers are always definite-length in MessagePack, so a reader loops a counted number
/// of entries and leaves the container with <see cref="Leave"/>.
/// </summary>
internal static class MsgPackWireRuntime
{
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    /// <summary>Opens a map and returns its entry count; counts as one level of nesting.</summary>
    public static int ReadMapStart(ref MessagePackReader r, MsgPackLimits l)
    {
        if (r.NextMessagePackType != MessagePackType.Map) throw new WireFormatException("Expected a MessagePack map.");
        var count = r.ReadMapHeader();
        Enter(ref r, l, count);
        return count;
    }

    /// <summary>Opens an array and returns its length; counts as one level of nesting.</summary>
    public static int ReadArrayStart(ref MessagePackReader r, MsgPackLimits l)
    {
        if (r.NextMessagePackType != MessagePackType.Array) throw new WireFormatException("Expected a MessagePack array.");
        var count = r.ReadArrayHeader();
        Enter(ref r, l, count);
        return count;
    }

    /// <summary>Closes the container opened by the last <c>Read*Start</c>.</summary>
    public static void Leave(ref MessagePackReader r) => r.Depth--;

    /// <summary>Reads one map key as its UTF-8 bytes. Keys must be strings.</summary>
    public static ReadOnlySpan<byte> ReadKey(ref MessagePackReader r, MsgPackLimits l)
    {
        if (r.NextMessagePackType != MessagePackType.String) throw new WireFormatException("A map key is not text.");
        return ReadUtf8(ref r, l);
    }

    /// <summary>Reads a string's bytes without decoding them, refusing one over the byte cap.</summary>
    public static ReadOnlySpan<byte> ReadUtf8(ref MessagePackReader r, MsgPackLimits l)
    {
        if (!r.TryReadStringSpan(out var span)) throw new WireFormatException("A string is split across buffers.");
        if (span.Length > l.MaxStringBytes) throw new WireFormatException("A string is longer than the decode limit.");
        return span;
    }

    public static string ReadText(ref MessagePackReader r, MsgPackLimits l)
    {
        if (r.NextMessagePackType != MessagePackType.String) throw new WireFormatException("Expected a string.");
        return _strictUtf8.GetString(ReadUtf8(ref r, l));
    }

    /// <summary>Reads text that may be nil, for optional envelope fields.</summary>
    public static string? ReadOptionalText(ref MessagePackReader r, MsgPackLimits l) => r.TryReadNil() ? null : ReadText(ref r, l);

    public static Guid ReadGuid(ref MessagePackReader r, MsgPackLimits l)
    {
        var text = ReadText(ref r, l);
        if (!Guid.TryParseExact(text, "D", out var guid)) throw new WireFormatException("A GUID is malformed.");
        return guid;
    }

    /// <summary>A DateTimeOffset is the ISO 8601 round-trip string, so a payload keeps its offset exactly as JSON does.</summary>
    public static void WriteDateTimeOffset(ref MessagePackWriter w, DateTimeOffset value) =>
        w.Write(value.ToString("O", CultureInfo.InvariantCulture));

    public static DateTimeOffset ReadDateTimeOffset(ref MessagePackReader r, MsgPackLimits l)
    {
        var text = ReadText(ref r, l);
        if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            throw new WireFormatException("A date-time is malformed.");
        }

        return value;
    }

    /// <summary>A decimal is its invariant text form: MessagePack has no decimal type and the text is exact.</summary>
    public static void WriteDecimal(ref MessagePackWriter w, decimal value) => w.Write(value.ToString(CultureInfo.InvariantCulture));

    public static decimal ReadDecimal(ref MessagePackReader r, MsgPackLimits l)
    {
        var text = ReadText(ref r, l);
        if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value))
        {
            throw new WireFormatException("A decimal is malformed.");
        }

        return value;
    }

    public static byte ReadByte(ref MessagePackReader r) => checked((byte)r.ReadUInt32());

    /// <summary>A float array is one <c>bin</c> of little-endian IEEE 754 binary32 values.</summary>
    public static void WriteFloat32Array(ref MessagePackWriter w, ReadOnlySpan<float> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            w.Write(MemoryMarshal.AsBytes(values));
            return;
        }

        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        w.Write(bytes);
    }

    public static float[] ReadFloat32Array(ref MessagePackReader r, MsgPackLimits l)
    {
        if (r.NextMessagePackType != MessagePackType.Binary) throw new WireFormatException("A float array is not a binary item.");
        var sequence = r.ReadBytes() ?? throw new WireFormatException("A float array is nil.");
        if (sequence.Length % sizeof(float) != 0) throw new WireFormatException("A float array is not a whole number of floats.");
        var count = sequence.Length / sizeof(float);
        if (count > l.MaxItems) throw new WireFormatException("A float array is longer than the decode limit.");
        var bytes = sequence.IsSingleSegment ? sequence.First.Span : sequence.ToArray();
        var values = new float[count];
        for (var i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * sizeof(float), sizeof(float)));
        return values;
    }

    /// <summary>Skips one item, enforcing depth and length limits. Unknown fields go through here.</summary>
    public static void SkipValue(ref MessagePackReader r, MsgPackLimits l)
    {
        switch (r.NextMessagePackType)
        {
            case MessagePackType.Array:
                {
                    var count = ReadArrayStart(ref r, l);
                    for (var i = 0; i < count; i++) SkipValue(ref r, l);
                    Leave(ref r);
                    break;
                }

            case MessagePackType.Map:
                {
                    var count = ReadMapStart(ref r, l);
                    for (var i = 0; i < count; i++)
                    {
                        SkipValue(ref r, l);
                        SkipValue(ref r, l);
                    }

                    Leave(ref r);
                    break;
                }

            case MessagePackType.String:
                _ = ReadUtf8(ref r, l);
                break;
            case MessagePackType.Binary:
                if ((r.ReadBytes() ?? default).Length > l.MaxStringBytes) throw new WireFormatException("A binary item is longer than the decode limit.");
                break;
            case MessagePackType.Extension:
                if (r.ReadExtensionFormat().Data.Length > l.MaxStringBytes) throw new WireFormatException("An extension item is longer than the decode limit.");
                break;
            default:
                r.Skip();
                break;
        }
    }

    public static WireFormatException MissingField(string key) => new($"Payload has no '{key}'.");

    public static WireFormatException DuplicateKey(string key) => new($"Map key '{key}' appears twice.");

    public static WireFormatException UnknownEnum(string type, string value) => new($"'{value}' is not a known {type}.");

    private static void Enter(ref MessagePackReader r, MsgPackLimits l, int count)
    {
        if (++r.Depth > l.MaxDepth) throw new WireFormatException("Data nests deeper than the decode limit.");
        if (count > l.MaxItems) throw new WireFormatException("A collection is longer than the decode limit.");
    }
}
