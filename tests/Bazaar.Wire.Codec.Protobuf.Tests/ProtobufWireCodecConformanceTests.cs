using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Conformance;

namespace Bazaar.Wire.Codec.Protobuf.Tests;

/// <summary>The Protobuf codec against the shared conformance suite (doc 90 Tests).</summary>
public class ProtobufWireCodecConformanceTests : WireCodecConformance
{
    protected override string ExpectedName => "protobuf";

    protected override bool ExpectedIsBinary => true;

    protected override IWireCodec CreateCodec(WireDecodeLimits limits) => new ProtobufWireCodec(limits);

    protected override byte[] AddUnknownField(ReadOnlyMemory<byte> encodedObject, int nestingDepth) =>
        ProtobufTestData.AddField(encodedObject, nestingDepth);
}

internal static class ProtobufTestData
{
    /// <summary>
    /// Appends an unknown field 1000 to an encoded message: a group nested <paramref name="depth"/> levels deep around one varint.
    /// Protobuf concatenation is merge, so appending is a valid way to add a field.
    /// </summary>
    public static byte[] AddField(ReadOnlyMemory<byte> encodedMessage, int depth)
    {
        var start = Tag(1000, 3);
        var end = Tag(1000, 4);
        var inner = new List<byte>();
        for (var i = 0; i < depth; i++) inner.AddRange(start);
        inner.AddRange(Tag(1, 0));
        inner.Add(0x01);
        for (var i = 0; i < depth; i++) inner.AddRange(end);
        return [.. encodedMessage.Span, .. start, .. inner, .. end];
    }

    public static byte[] Tag(int field, int wireType)
    {
        var value = (uint)((field << 3) | wireType);
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return [.. bytes];
    }
}
