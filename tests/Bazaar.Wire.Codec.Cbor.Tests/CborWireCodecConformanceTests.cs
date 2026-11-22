using System.Formats.Cbor;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Conformance;

namespace Bazaar.Wire.Codec.Cbor.Tests;

/// <summary>The CBOR codec against the shared conformance suite (doc 90 Tests).</summary>
public class CborWireCodecConformanceTests : WireCodecConformance
{
    protected override string ExpectedName => "cbor";

    protected override bool ExpectedIsBinary => true;

    protected override IWireCodec CreateCodec(WireDecodeLimits limits) => new CborWireCodec(limits);

    protected override byte[] AddUnknownField(ReadOnlyMemory<byte> encodedObject, int nestingDepth) =>
        CborTestData.AddField(encodedObject, "x-future", CborTestData.Nested(nestingDepth));
}

internal static class CborTestData
{
    /// <summary>An encoded array nested <paramref name="depth"/> levels deep around the integer 1.</summary>
    public static byte[] Nested(int depth)
    {
        var bytes = new byte[depth + 1];
        Array.Fill(bytes, (byte)0x81, 0, depth);
        bytes[depth] = 0x01;
        return bytes;
    }

    /// <summary>
    /// Appends one entry to an encoded definite-length map by byte surgery, so the value may be one the writer would refuse
    /// (a nesting bomb). Maps here have fewer than 24 entries and keys shorter than 24 bytes.
    /// </summary>
    public static byte[] AddField(ReadOnlyMemory<byte> encodedMap, string key, byte[] encodedValue)
    {
        var map = encodedMap.Span;
        Assert.Equal(0xA0, map[0] & 0xE0);
        var count = map[0] & 0x1F;
        Assert.True(count < 23, "Test helper handles maps of up to 22 entries.");
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        Assert.True(keyBytes.Length < 24);
        return
        [
            (byte)(0xA0 | (count + 1)),
            .. map[1..],
            (byte)(0x60 | keyBytes.Length),
            .. keyBytes,
            .. encodedValue,
        ];
    }
}
