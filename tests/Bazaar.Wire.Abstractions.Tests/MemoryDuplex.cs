using System.IO.Pipelines;

namespace Bazaar.Wire.Abstractions.Tests;

/// <summary>Two connected in-memory duplex streams standing in for a QUIC stream pair.</summary>
internal sealed class MemoryDuplex : Stream
{
    private readonly Stream _read;
    private readonly Stream _write;

    private MemoryDuplex(Stream read, Stream write)
    {
        _read = read;
        _write = write;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public static (MemoryDuplex A, MemoryDuplex B) Create()
    {
        var ab = new Pipe();
        var ba = new Pipe();
        return (new MemoryDuplex(ba.Reader.AsStream(), ab.Writer.AsStream()), new MemoryDuplex(ab.Reader.AsStream(), ba.Writer.AsStream()));
    }

    public override void Flush() => _write.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _write.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _write.Dispose();
            _read.Dispose();
        }

        base.Dispose(disposing);
    }
}
