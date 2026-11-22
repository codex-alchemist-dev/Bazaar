using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Conformance;
using Xunit;

namespace Bazaar.Wire.Codec.MessagePack.Tests;

/// <summary>The MessagePack codec against the shared conformance suite (doc 90 Tests).</summary>
public class MsgPackWireCodecConformanceTests : WireCodecConformance
{
    protected override string ExpectedName => "msgpack";

    protected override bool ExpectedIsBinary => true;

    protected override IWireCodec CreateCodec(WireDecodeLimits limits) => new MsgPackWireCodec(limits);

    protected override byte[] AddUnknownField(ReadOnlyMemory<byte> encodedObject, int nestingDepth) =>
        MsgPackTestData.AddField(encodedObject, "x-future", MsgPackTestData.Nested(nestingDepth));
}

internal static class MsgPackTestData
{
    /// <summary>An encoded array nested <paramref name="depth"/> levels deep around the integer 1.</summary>
    public static byte[] Nested(int depth)
    {
        var bytes = new byte[depth + 1];
        Array.Fill(bytes, (byte)0x91, 0, depth);
        bytes[depth] = 0x01;
        return bytes;
    }

    /// <summary>
    /// Appends one entry to an encoded fixmap by byte surgery, so the value may be one the writer would refuse (a nesting bomb).
    /// Maps here have fewer than 15 entries and keys shorter than 32 bytes.
    /// </summary>
    public static byte[] AddField(ReadOnlyMemory<byte> encodedMap, string key, byte[] encodedValue)
    {
        var map = encodedMap.Span;
        Assert.Equal(0x80, map[0] & 0xF0);
        var count = map[0] & 0x0F;
        Assert.True(count < 15, "Test helper handles maps of up to 14 entries.");
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        Assert.True(keyBytes.Length < 32);
        return
        [
            (byte)(0x80 | (count + 1)),
            .. map[1..],
            (byte)(0xA0 | keyBytes.Length),
            .. keyBytes,
            .. encodedValue,
        ];
    }
}
