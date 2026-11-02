using Bazaar.Server.Vms.Images;

namespace Bazaar.Server.Vms.Tests.Images;

/// <summary>
/// Wraps a stream and returns at most <see cref="MaxChunk"/> bytes per read, so tests can check that hashing reads a file
/// in pieces rather than in one call.
/// </summary>
internal sealed class ChunkedReadStream(Stream inner, int maxChunk) : Stream
{
    public int MaxChunk { get; } = maxChunk;

    public int ReadCalls { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCalls++;
        return inner.Read(buffer, offset, Math.Min(count, MaxChunk));
    }

    public override int Read(Span<byte> buffer)
    {
        ReadCalls++;
        return inner.Read(buffer[..Math.Min(buffer.Length, MaxChunk)]);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
