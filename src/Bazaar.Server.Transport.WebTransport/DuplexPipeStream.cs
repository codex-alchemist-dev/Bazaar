using System.IO.Pipelines;

namespace Bazaar.Server.Transport.WebTransport;

/// <summary>A read/write <see cref="Stream"/> over a Kestrel <see cref="IDuplexPipe"/> (one WebTransport bidirectional stream).</summary>
internal sealed class DuplexPipeStream(IDuplexPipe pipe) : Stream
{
    private readonly Stream _in = pipe.Input.AsStream();
    private readonly Stream _out = pipe.Output.AsStream();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _out.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _out.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _in.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _out.WriteAsync(buffer, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        await pipe.Output.CompleteAsync().ConfigureAwait(false);
        await pipe.Input.CompleteAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
