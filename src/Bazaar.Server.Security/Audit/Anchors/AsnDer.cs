using System.Text;

namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// Minimal DER encoder (ITU-T X.690) for the RFC 3161 request. It writes only the types the request uses, with definite
/// lengths and minimal integers, so the same input always gives the same bytes.
/// </summary>
internal static class AsnDer
{
    public const byte BooleanTag = 0x01;
    public const byte IntegerTag = 0x02;
    public const byte OctetStringTag = 0x04;
    public const byte NullTag = 0x05;
    public const byte ObjectIdentifierTag = 0x06;
    public const byte SequenceTag = 0x30;

    /// <summary>Encodes one tag-length-value element.</summary>
    public static byte[] Tlv(byte tag, ReadOnlySpan<byte> content)
    {
        using var stream = new MemoryStream(content.Length + 6);
        stream.WriteByte(tag);
        WriteLength(stream, content.Length);
        stream.Write(content);
        return stream.ToArray();
    }

    /// <summary>A SEQUENCE over the already-encoded items, in the order given.</summary>
    public static byte[] Sequence(params byte[][] items) => Tlv(SequenceTag, Concat(items));

    /// <summary>An explicitly tagged context-specific element, as used for <c>[0] EXPLICIT</c> fields.</summary>
    public static byte[] ContextExplicit(int number, ReadOnlySpan<byte> inner)
    {
        if (number < 0 || number > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, "Only low-numbered context tags are supported.");
        }

        return Tlv((byte)(0xA0 | number), inner);
    }

    public static byte[] Boolean(bool value) => Tlv(BooleanTag, value ? [0xFF] : [0x00]);

    public static byte[] Null() => Tlv(NullTag, ReadOnlySpan<byte>.Empty);

    public static byte[] OctetString(ReadOnlySpan<byte> value) => Tlv(OctetStringTag, value);

    /// <summary>
    /// An INTEGER from an unsigned big-endian value. Leading zero bytes are dropped, and a 0x00 is added when the high bit
    /// would otherwise make the value negative.
    /// </summary>
    public static byte[] Integer(ReadOnlySpan<byte> unsignedBigEndian)
    {
        if (unsignedBigEndian.Length == 0)
        {
            return Tlv(IntegerTag, [0x00]);
        }

        var start = 0;
        while (start < unsignedBigEndian.Length - 1 && unsignedBigEndian[start] == 0)
        {
            start++;
        }

        var value = unsignedBigEndian[start..];
        if ((value[0] & 0x80) != 0)
        {
            var padded = new byte[value.Length + 1];
            value.CopyTo(padded.AsSpan(1));
            return Tlv(IntegerTag, padded);
        }

        return Tlv(IntegerTag, value);
    }

    /// <summary>An OBJECT IDENTIFIER from its dotted form, for example <c>2.16.840.1.101.3.4.2.1</c>.</summary>
    public static byte[] ObjectIdentifier(string dotted)
    {
        var arcs = dotted.Split('.').Select(arc => long.Parse(arc, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (arcs.Length < 2 || arcs[0] > 2 || (arcs[0] < 2 && arcs[1] >= 40))
        {
            throw new ArgumentException("Not a valid object identifier.", nameof(dotted));
        }

        using var stream = new MemoryStream();
        WriteBase128(stream, arcs[0] * 40 + arcs[1]);
        for (var i = 2; i < arcs.Length; i++)
        {
            WriteBase128(stream, arcs[i]);
        }

        return Tlv(ObjectIdentifierTag, stream.ToArray());
    }

    private static void WriteLength(MemoryStream stream, int length)
    {
        if (length < 0x80)
        {
            stream.WriteByte((byte)length);
            return;
        }

        var bytes = new List<byte>();
        for (var remaining = length; remaining > 0; remaining >>= 8)
        {
            bytes.Insert(0, (byte)(remaining & 0xFF));
        }

        stream.WriteByte((byte)(0x80 | bytes.Count));
        stream.Write(bytes.ToArray());
    }

    private static void WriteBase128(MemoryStream stream, long value)
    {
        var groups = new List<byte>();
        var remaining = value;
        do
        {
            groups.Insert(0, (byte)(remaining & 0x7F));
            remaining >>= 7;
        }
        while (remaining > 0);

        for (var i = 0; i < groups.Count - 1; i++)
        {
            groups[i] |= 0x80;
        }

        stream.Write(groups.ToArray());
    }

    private static byte[] Concat(byte[][] items)
    {
        var total = items.Sum(item => item.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var item in items)
        {
            item.CopyTo(result, offset);
            offset += item.Length;
        }

        return result;
    }
}

/// <summary>One DER element as read: its tag, its full encoding (tag, length and content) and its content bytes.</summary>
internal readonly record struct AsnElement(byte Tag, byte[] Encoded, byte[] Content);

/// <summary>
/// Reads DER elements in order from one buffer. Accepts definite lengths only, in their minimal form. Throws
/// <see cref="FormatException"/> on anything else, including truncated input, so a hostile response cannot read past its end.
/// </summary>
internal sealed class AsnReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _offset;

    public AsnReader(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _end = data.Length;
    }

    private AsnReader(byte[] data, int offset, int end)
    {
        _data = data;
        _offset = offset;
        _end = end;
    }

    /// <summary>True while unread bytes remain in this buffer.</summary>
    public bool HasMore => _offset < _end;

    /// <summary>Reads the next element and advances past it.</summary>
    public AsnElement Read()
    {
        if (_end - _offset < 2)
        {
            throw new FormatException("Truncated DER element.");
        }

        var start = _offset;
        var tag = _data[_offset++];
        if ((tag & 0x1F) == 0x1F)
        {
            throw new FormatException("High-tag-number form is not supported.");
        }

        var length = ReadLength();
        if (length > _end - _offset)
        {
            throw new FormatException("DER length runs past its container.");
        }

        var contentStart = _offset;
        _offset += length;
        return new AsnElement(tag, _data[start.._offset], _data[contentStart.._offset]);
    }

    /// <summary>Reads the next element, checks its tag and returns a reader over its content.</summary>
    public AsnReader Open(byte expectedTag)
    {
        return new AsnReader(Expect(Read(), expectedTag).Content);
    }

    /// <summary>Checks the tag of an element that was already read.</summary>
    public static AsnElement Expect(AsnElement element, byte expectedTag) =>
        element.Tag == expectedTag ? element : throw new FormatException($"Unexpected DER tag 0x{element.Tag:X2}.");

    private int ReadLength()
    {
        if (_offset >= _end)
        {
            throw new FormatException("Truncated DER length.");
        }

        var first = _data[_offset++];
        if (first < 0x80)
        {
            return first;
        }

        if (first == 0x80)
        {
            throw new FormatException("Indefinite lengths are not DER.");
        }

        var count = first & 0x7F;
        if (count > 4 || _end - _offset < count)
        {
            throw new FormatException("Unsupported or truncated DER length.");
        }

        var length = 0;
        for (var i = 0; i < count; i++)
        {
            length = (length << 8) | _data[_offset++];
        }

        if (length < 0x80 || (count > 1 && length < 1 << (8 * (count - 1))))
        {
            throw new FormatException("DER length is not minimal.");
        }

        return length;
    }
}
