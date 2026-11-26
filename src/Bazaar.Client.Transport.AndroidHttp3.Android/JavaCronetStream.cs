using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Android;

/// <summary>
/// One Cronet stream as <see cref="ICronetBidirectionalStream"/>: callbacks become awaitable operations. At most one read
/// and one write are in flight (Cronet's rule); a read is requested only when the consumer asks, so a slow consumer
/// stalls the network instead of buffering. Cronet has no per-operation cancel, so a cancelled token cancels the stream.
/// </summary>
internal sealed class JavaCronetStream : ICronetBidirectionalStream, IJavaCronetCallback
{
    private const int _readChunkBytes = 16 * 1024;

    private readonly IJavaCronetBridge _bridge;
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _ready = NewSource();
    private readonly TaskCompletionSource<CronetResponseHead> _head = NewSource<CronetResponseHead>();
    private readonly TaskCompletionSource _terminal = NewSource();
    private IJavaCronetCall? _java;
    private TaskCompletionSource<ReadOnlyMemory<byte>>? _read;
    private TaskCompletionSource? _write;
    private ReadOnlyMemory<byte> _leftover;
    private bool _bodyEnded;
    private bool _finished;
    private bool _disposed;
    private IReadOnlyDictionary<string, string>? _trailers;
    private Exception? _failure;

    public JavaCronetStream(IJavaCronetBridge bridge)
    {
        _bridge = bridge;
        _bridge.NetworkEvent += Relay;
    }

    public event Action<WireNetworkEvent>? NetworkEvent;

    public IReadOnlyDictionary<string, string> Trailers => _trailers ?? new Dictionary<string, string>();

    public bool EndedCleanly => _trailers is not null;

    public async ValueTask StartAsync(CronetStreamRequest request, CancellationToken cancellationToken)
    {
        _java = _bridge.Start(request, this);
        await WaitAsync(_ready.Task, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CronetResponseHead> ReadResponseHeadAsync(CancellationToken cancellationToken) =>
        await WaitAsync(_head.Task, cancellationToken).ConfigureAwait(false);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        await WriteCoreAsync(data, endOfStream: false, cancellationToken).ConfigureAwait(false);

    public async ValueTask CompleteWritesAsync(CancellationToken cancellationToken) =>
        await WriteCoreAsync(ReadOnlyMemory<byte>.Empty, endOfStream: true, cancellationToken).ConfigureAwait(false);

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty) return 0;
        if (!_leftover.IsEmpty) return TakeLeftover(buffer);
        if (_bodyEnded) return await FinishAsync(cancellationToken).ConfigureAwait(false);

        TaskCompletionSource<ReadOnlyMemory<byte>> pending;
        lock (_gate)
        {
            ThrowIfUnusable();
            if (_read is not null) throw new InvalidOperationException("A read is already in flight.");
            pending = _read = NewSource<ReadOnlyMemory<byte>>();
        }

        _java!.RequestRead(Math.Min(buffer.Length, _readChunkBytes));
        var data = await WaitAsync(pending.Task, cancellationToken).ConfigureAwait(false);
        _leftover = data;
        if (!_leftover.IsEmpty) return TakeLeftover(buffer);
        return _bodyEnded ? await FinishAsync(cancellationToken).ConfigureAwait(false) : 0;
    }

    public async ValueTask DisposeAsync()
    {
        bool cancel;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            cancel = !_finished;
        }

        _bridge.NetworkEvent -= Relay;
        if (cancel) _java?.Cancel();
        Fail(new ObjectDisposedException(nameof(JavaCronetStream)));
        if (cancel && _java is not null)
        {
            // Wait for Cronet's terminal callback so its buffers are released; it always arrives after cancel().
            try
            {
                await _terminal.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                // The terminal callback is late or failed; the stream is cancelled either way.
            }
        }
    }

    void IJavaCronetCallback.OnStreamReady() => _ready.TrySetResult();

    void IJavaCronetCallback.OnResponseHeaders(int httpStatus, string negotiatedProtocol, IReadOnlyDictionary<string, string> headers) =>
        _head.TrySetResult(new CronetResponseHead(httpStatus, negotiatedProtocol, headers));

    void IJavaCronetCallback.OnReadCompleted(ReadOnlyMemory<byte> data, bool endOfStream)
    {
        TaskCompletionSource<ReadOnlyMemory<byte>>? pending;
        lock (_gate)
        {
            pending = _read;
            _read = null;
            if (endOfStream) _bodyEnded = true;
        }

        pending?.TrySetResult(data);
    }

    void IJavaCronetCallback.OnWriteCompleted()
    {
        TaskCompletionSource? pending;
        lock (_gate)
        {
            pending = _write;
            _write = null;
        }

        pending?.TrySetResult();
    }

    void IJavaCronetCallback.OnTrailers(IReadOnlyDictionary<string, string> trailers) => _trailers = trailers;

    void IJavaCronetCallback.OnSucceeded()
    {
        lock (_gate) _finished = true;
        _terminal.TrySetResult();
        Fail(new IOException("The stream ended."), keepTerminalOk: true);
    }

    void IJavaCronetCallback.OnFailed(Exception failure)
    {
        lock (_gate) _finished = true;
        var io = new IOException("The Cronet stream failed.", failure);
        _failure = io;
        _terminal.TrySetException(io);
        Fail(io);
    }

    void IJavaCronetCallback.OnCanceled()
    {
        lock (_gate) _finished = true;
        var io = new IOException("The Cronet stream was cancelled.");
        _failure ??= io;
        _terminal.TrySetResult();
        Fail(io);
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Relay(WireNetworkEvent networkEvent) => NetworkEvent?.Invoke(networkEvent);

    private int TakeLeftover(Memory<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _leftover.Length);
        _leftover[..count].CopyTo(buffer);
        _leftover = _leftover[count..];
        return count;
    }

    // The body is over; 0 is only valid once the trailers are final, which is at Cronet's terminal callback.
    private async ValueTask<int> FinishAsync(CancellationToken cancellationToken)
    {
        await WaitAsync(_terminal.Task, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool endOfStream, CancellationToken cancellationToken)
    {
        TaskCompletionSource pending;
        lock (_gate)
        {
            ThrowIfUnusable();
            if (_write is not null) throw new InvalidOperationException("A write is already in flight.");
            pending = _write = NewSource();
        }

        _java!.Write(data, endOfStream);
        await WaitAsync(pending.Task, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null) throw new IOException(_failure.Message, _failure);
    }

    private async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _java?.Cancel();
            throw;
        }
    }

    private async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _java?.Cancel();
            throw;
        }
    }

    // Wakes every waiter: reads and writes in flight, and the head and ready waits.
    private void Fail(Exception error, bool keepTerminalOk = false)
    {
        TaskCompletionSource<ReadOnlyMemory<byte>>? read;
        TaskCompletionSource? write;
        lock (_gate)
        {
            read = _read;
            write = _write;
            _read = null;
            _write = null;
        }

        if (keepTerminalOk)
        {
            // A clean end: a read waiting at that point sees end of body; the others have nothing left to wait for.
            read?.TrySetResult(ReadOnlyMemory<byte>.Empty);
            _bodyEnded = true;
        }
        else
        {
            read?.TrySetException(error);
        }

        write?.TrySetException(error);
        _ready.TrySetException(error);
        _head.TrySetException(error);
    }
}
