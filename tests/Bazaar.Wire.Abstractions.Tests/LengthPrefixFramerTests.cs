using System.Buffers;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Wire.Abstractions.Tests;

public sealed class LengthPrefixFramerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(100000)]
    public void RoundTrip(int length)
    {
        var frame = new byte[length];
        new Random(length).NextBytes(frame);
        var writer = new ArrayBufferWriter<byte>();
        LengthPrefixFramer.Write(frame, writer, 1 << 20);
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(LengthPrefixFramer.TryRead(ref buffer, 1 << 20, out var read));
        Assert.Equal(frame, read.ToArray());
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void PartialFrame_NeedsMoreBytes_AndLeavesBufferAlone()
    {
        var writer = new ArrayBufferWriter<byte>();
        LengthPrefixFramer.Write(new byte[200], writer, 1000);
        var cut = new ReadOnlySequence<byte>(writer.WrittenMemory[..100]);
        Assert.False(LengthPrefixFramer.TryRead(ref cut, 1000, out _));
        Assert.Equal(100, cut.Length);
    }

    [Fact]
    public void OversizedDeclaredLength_ThrowsBeforeBufferingPayload()
    {
        var writer = new ArrayBufferWriter<byte>();
        LengthPrefixFramer.Write(new byte[5000], writer, 10000);
        var header = new ReadOnlySequence<byte>(writer.WrittenMemory[..2]);
        Assert.Throws<InvalidDataException>(() => LengthPrefixFramer.TryRead(ref header, 1000, out _));
    }

    [Fact]
    public void WriteRefusesFrameAboveMaximum() =>
        Assert.Throws<ArgumentException>(() => LengthPrefixFramer.Write(new byte[11], new ArrayBufferWriter<byte>(), 10));

    [Fact]
    public void TwoFramesBackToBack()
    {
        var writer = new ArrayBufferWriter<byte>();
        LengthPrefixFramer.Write([1, 2], writer, 100);
        LengthPrefixFramer.Write([3], writer, 100);
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(LengthPrefixFramer.TryRead(ref buffer, 100, out var a));
        Assert.True(LengthPrefixFramer.TryRead(ref buffer, 100, out var b));
        Assert.Equal([1, 2], a.ToArray());
        Assert.Equal([3], b.ToArray());
    }
}
