namespace Bazaar.Vms.Sockets;

/// <summary>
/// A duplex stream over a connected stream-socket descriptor. Reads wait with poll in short slices so a cancelled token or a dispose ends them;
/// the blocking libc calls run on the thread pool. Dispose shuts the socket down first, which wakes a blocked reader with end of stream.
/// </summary>
internal sealed class VsockStream : Stream
{
    private const int _pollSliceMs = 200;
    private readonly LinuxVsock.FdHandle _handle;

    /// <summary>Creates a stream that owns <paramref name="handle"/>.</summary>
    public VsockStream(LinuxVsock.FdHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => LinuxVsock.SendAll(_handle, buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => ReadCore(buffer.Span, cancellationToken), cancellationToken));

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Task.Run(() => LinuxVsock.SendAll(_handle, buffer.Span), CancellationToken.None));
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_handle.IsClosed)
        {
            LinuxVsock.Shutdown(_handle);
            _handle.Dispose();
        }

        base.Dispose(disposing);
    }

    private int ReadCore(Span<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_handle.IsClosed)
            {
                return 0;
            }

            try
            {
                if (LinuxVsock.WaitReadable(_handle, _pollSliceMs))
                {
                    return LinuxVsock.Receive(_handle, buffer);
                }
            }
            catch (ObjectDisposedException)
            {
                // Disposed between the check and the call: the stream is over.
                return 0;
            }
        }
    }
}
