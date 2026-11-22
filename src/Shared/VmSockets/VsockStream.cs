using System.Threading.Channels;

namespace Bazaar.Vms.Sockets;

/// <summary>
/// A duplex stream over a connected stream-socket descriptor. One dedicated long-running thread pumps reads into a bounded queue and one pumps
/// writes out of a queue, so a connection never ties up pool threads per call. The read pump polls in short slices so a dispose ends it, and
/// dispose shuts the socket down first, which also wakes a peer blocked on it with end of stream.
/// </summary>
internal sealed class VsockStream : Stream
{
    private const int _pollSliceMs = 200;
    private const int _readChunkBytes = 16 * 1024;
    private const int _queuedChunks = 16;

    private readonly LinuxVsock.FdHandle _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_queuedChunks)
    {
        SingleReader = true,
        SingleWriter = true,
    });

    private readonly Channel<WriteRequest> _outgoing = Channel.CreateUnbounded<WriteRequest>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _readPump;
    private readonly Task _writePump;
    private byte[] _current = [];
    private int _currentOffset;
    private int _disposed;

    /// <summary>Creates a stream that owns <paramref name="handle"/> and starts its two pumps.</summary>
    public VsockStream(LinuxVsock.FdHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
        _readPump = Task.Factory.StartNew(ReadPump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _writePump = Task.Factory.StartNew(WritePump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_currentOffset >= _current.Length)
        {
            try
            {
                if (!await _incoming.Reader.WaitToReadAsync(cancellationToken) || !_incoming.Reader.TryRead(out var next))
                {
                    return 0;
                }

                _current = next;
                _currentOffset = 0;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
        _current.AsMemory(_currentOffset, count).CopyTo(buffer);
        _currentOffset += count;
        return count;
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        var request = new WriteRequest(buffer.ToArray());
        if (!_outgoing.Writer.TryWrite(request))
        {
            throw new IOException("The connection is closed.");
        }

        await request.Done.Task.WaitAsync(cancellationToken);
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
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Cancel();
            _outgoing.Writer.TryComplete();
            LinuxVsock.Shutdown(_handle);

            // The pumps leave their loops on the shutdown and the stop token. The handle closes once they are done with it.
            _ = Task.WhenAll(_readPump, _writePump).ContinueWith(
                _ =>
                {
                    _handle.Dispose();
                    _stop.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        base.Dispose(disposing);
    }

    private void ReadPump()
    {
        try
        {
            var buffer = new byte[_readChunkBytes];
            while (!_stop.IsCancellationRequested)
            {
                if (!LinuxVsock.WaitReadable(_handle, _pollSliceMs))
                {
                    continue;
                }

                var count = LinuxVsock.Receive(_handle, buffer);
                if (count == 0)
                {
                    break;
                }

                // A reader that falls behind blocks this thread, which is the backpressure the peer needs.
                _incoming.Writer.WriteAsync(buffer.AsMemory(0, count).ToArray(), _stop.Token).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection broke or the stream was disposed. Either way the reader sees the end.
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    private void WritePump()
    {
        Exception? failure = null;
        try
        {
            while (_outgoing.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_outgoing.Reader.TryRead(out var request))
                {
                    if (failure is not null)
                    {
                        request.Done.TrySetException(failure);
                        continue;
                    }

                    try
                    {
                        LinuxVsock.SendAll(_handle, request.Bytes);
                        request.Done.TrySetResult();
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        failure = ex;
                        request.Done.TrySetException(ex);
                    }
                }
            }
        }
        finally
        {
            while (_outgoing.Reader.TryRead(out var request))
            {
                request.Done.TrySetException(failure ?? new IOException("The connection is closed."));
            }
        }
    }

    private sealed class WriteRequest
    {
        public WriteRequest(byte[] bytes)
        {
            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
