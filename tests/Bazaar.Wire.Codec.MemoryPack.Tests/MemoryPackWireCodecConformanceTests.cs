using System.Buffers;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Conformance;
using MemoryPack;

namespace Bazaar.Wire.Codec.MemoryPack.Tests;

/// <summary>The MemoryPack codec against the shared conformance suite (doc 90 Tests).</summary>
public class MemoryPackWireCodecConformanceTests : WireCodecConformance
{
    protected override string ExpectedName => "memorypack";

    protected override bool ExpectedIsBinary => true;

    protected override bool UnknownFieldsAreOpaque => true;

    protected override IWireCodec CreateCodec(WireDecodeLimits limits) => new MemoryPackWireCodec(limits);

    protected override byte[] AddUnknownField(ReadOnlyMemory<byte> encodedObject, int nestingDepth) =>
        MemoryPackTestData.AddMember(encodedObject.Span, new byte[nestingDepth + 1]);
}

internal static class MemoryPackTestData
{
    /// <summary>Appends one member to an encoded version-tolerant object, as a later minor version would.</summary>
    public static byte[] AddMember(ReadOnlySpan<byte> encodedObject, ReadOnlySpan<byte> memberBytes)
    {
        var data = encodedObject.ToArray();
        using var readState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var reader = new MemoryPackReader(data, readState);
        Assert.True(reader.TryReadObjectHeader(out var count));
        var lengths = new int[count];
        for (var i = 0; i < count; i++) lengths[i] = reader.ReadVarIntInt32();
        var body = data.AsSpan(reader.Consumed).ToArray();

        var buffer = new ArrayBufferWriter<byte>();
        using var writeState = MemoryPackWriterOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var writer = new MemoryPackWriter<ArrayBufferWriter<byte>>(ref buffer, writeState);
        writer.WriteObjectHeader((byte)(count + 1));
        foreach (var length in lengths) writer.WriteVarInt(length);
        writer.WriteVarInt(memberBytes.Length);
        writer.WriteSpanWithoutLengthHeader(body.AsSpan());
        writer.WriteSpanWithoutLengthHeader(memberBytes);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
