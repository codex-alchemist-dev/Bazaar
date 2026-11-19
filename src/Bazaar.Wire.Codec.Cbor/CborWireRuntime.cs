using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Runtime.InteropServices;
using System.Text;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Wire.Codec.Cbor;

/// <summary>
/// Helpers the generated CBOR readers and writers call. Every decode limit lives here, so a generated reader cannot forget
/// one: depth, string length and collection length are checked as each container or string is read (doc 90 section 4.5).
/// </summary>
internal static class CborWireRuntime
{
    /// <summary>RFC 8746 tag for a typed array of little-endian IEEE 754 binary32 values.</summary>
    private const CborTag Float32LittleEndianArray = (CborTag)85;

    public static void ReadMapStart(CborReader r, CborLimits l)
    {
        if (r.PeekState() != CborReaderState.StartMap) throw new WireFormatException("Expected a CBOR map.");
        var count = r.ReadStartMap();
        EnterContainer(r, l, count);
    }

    public static void ReadArrayStart(CborReader r, CborLimits l)
    {
        if (r.PeekState() != CborReaderState.StartArray) throw new WireFormatException("Expected a CBOR array.");
        var count = r.ReadStartArray();
        EnterContainer(r, l, count);
    }

    /// <summary>Moves to the next map key, or past the end of the map. Keys must be text.</summary>
    public static bool NextKey(CborReader r, CborLimits l, ref int items, out string key)
    {
        if (r.PeekState() == CborReaderState.EndMap)
        {
            r.ReadEndMap();
            key = string.Empty;
            return false;
        }

        if (++items > l.MaxItems) throw new WireFormatException("A map has more entries than the decode limit.");
        if (r.PeekState() != CborReaderState.TextString) throw new WireFormatException("A map key is not text.");
        key = ReadText(r, l);
        return true;
    }

    /// <summary>Moves to the next array item, or past the end of the array.</summary>
    public static bool NextItem(CborReader r, CborLimits l, ref int items)
    {
        if (r.PeekState() == CborReaderState.EndArray)
        {
            r.ReadEndArray();
            return false;
        }

        if (++items > l.MaxItems) throw new WireFormatException("An array is longer than the decode limit.");
        return true;
    }

    public static string ReadText(CborReader r, CborLimits l)
    {
        var text = r.ReadTextString();
        // Characters never outnumber bytes, so the byte count is only worth computing for long strings.
        if (text.Length > l.MaxStringBytes / 3 && Encoding.UTF8.GetByteCount(text) > l.MaxStringBytes)
        {
            throw new WireFormatException("A string is longer than the decode limit.");
        }

        return text;
    }

    public static Guid ReadGuid(CborReader r, CborLimits l)
    {
        var text = ReadText(r, l);
        if (!Guid.TryParseExact(text, "D", out var guid)) throw new WireFormatException("A GUID is malformed.");
        return guid;
    }

    public static byte ReadByte(CborReader r) => checked((byte)r.ReadUInt32());

    public static void WriteFloat32Array(CborWriter w, ReadOnlySpan<float> values)
    {
        w.WriteTag(Float32LittleEndianArray);
        if (BitConverter.IsLittleEndian)
        {
            w.WriteByteString(MemoryMarshal.AsBytes(values));
            return;
        }

        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        w.WriteByteString(bytes);
    }

    public static float[] ReadFloat32Array(CborReader r, CborLimits l)
    {
        if (r.ReadTag() != Float32LittleEndianArray) throw new WireFormatException("A float array does not carry the little-endian binary32 tag.");
        var bytes = r.ReadByteString();
        if (bytes.Length % sizeof(float) != 0) throw new WireFormatException("A float array is not a whole number of floats.");
        var count = bytes.Length / sizeof(float);
        if (count > l.MaxItems) throw new WireFormatException("A float array is longer than the decode limit.");
        var values = new float[count];
        for (var i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        return values;
    }

    /// <summary>Skips one data item, enforcing depth and length limits. Unknown fields go through here.</summary>
    public static void SkipValue(CborReader r, CborLimits l)
    {
        switch (r.PeekState())
        {
            case CborReaderState.StartArray:
                {
                    ReadArrayStart(r, l);
                    var items = 0;
                    while (NextItem(r, l, ref items)) SkipValue(r, l);
                    break;
                }

            case CborReaderState.StartMap:
                {
                    ReadMapStart(r, l);
                    var items = 0;
                    while (r.PeekState() != CborReaderState.EndMap)
                    {
                        if (++items > l.MaxItems) throw new WireFormatException("A map has more entries than the decode limit.");
                        SkipValue(r, l);
                        SkipValue(r, l);
                    }

                    r.ReadEndMap();
                    break;
                }

            case CborReaderState.Tag:
                {
                    // A chain of tags nests without containers, so it counts against the depth cap.
                    var tags = 0;
                    while (r.PeekState() == CborReaderState.Tag)
                    {
                        r.ReadTag();
                        if (++tags > l.MaxDepth) throw new WireFormatException("Tags nest deeper than the decode limit.");
                    }

                    SkipValue(r, l);
                    break;
                }

            case CborReaderState.TextString:
                _ = ReadText(r, l);
                break;
            case CborReaderState.ByteString:
                if (r.ReadByteString().Length > l.MaxStringBytes) throw new WireFormatException("A byte string is longer than the decode limit.");
                break;
            default:
                r.SkipValue();
                break;
        }
    }

    public static WireFormatException MissingField(string key) => new($"Payload has no '{key}'.");

    public static WireFormatException UnknownEnum(string type, string value) => new($"'{value}' is not a known {type}.");

    private static void EnterContainer(CborReader r, CborLimits l, int? count)
    {
        if (r.CurrentDepth > l.MaxDepth) throw new WireFormatException("Data nests deeper than the decode limit.");
        if (count > l.MaxItems) throw new WireFormatException("A collection is longer than the decode limit.");
    }
}
