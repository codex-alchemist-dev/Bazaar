using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay;

/// <summary>A write-only in-memory stream that throws <see cref="DisplayFailure.FrameTooLarge"/> as soon as a write would pass the cap.</summary>
internal sealed class CappedSinkStream : Stream
{
    private readonly MemoryStream _inner = new();
    private readonly int _cap;

    public CappedSinkStream(int cap)
    {
        _cap = cap;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    /// <summary>The bytes written so far.</summary>
    public byte[] ToArray() => _inner.ToArray();

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_inner.Length + buffer.Length > _cap)
            throw new DisplayException(DisplayFailure.FrameTooLarge, $"The guest file is larger than the {_cap} byte limit.");
        _inner.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
