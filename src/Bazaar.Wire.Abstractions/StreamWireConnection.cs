using System.Buffers;

namespace Bazaar.Wire.Abstractions;

/// <summary>Limits and hooks for <see cref="StreamWireConnection"/>.</summary>
public sealed class StreamWireConnectionOptions
{
    /// <summary>Largest frame in bytes (doc 02 <c>wire.max_frame_kib</c>, default 256 KiB). Checked on the declared length before buffering.</summary>
    public int MaxFrameBytes { get; init; } = 256 * 1024;

    /// <summary>Largest first received frame (the <c>hello</c>) in bytes: 8 KiB (doc 90 section 4.5). A server-side guard; a client sets <see cref="int.MaxValue"/>.</summary>
    public int PreHelloMaxBytes { get; init; } = 8 * 1024;

    /// <summary>Time allowed for the first frame; null for no limit. Default 5 s (<c>wire.hello_timeout_s</c>). Expiry closes with 4408.</summary>
    public TimeSpan? PreHelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Closes the underlying transport with the given close code (QUIC: application error code, same number). Null when disposing the stream is enough.</summary>
    public Func<int, ValueTask>? CloseAsync { get; init; }

    /// <summary>Maps a transport exception to the close code the peer used, or null when it is no close. Null means any <see cref="IOException"/> is a plain close (0).</summary>
    public Func<Exception, int?>? CloseCodeFromException { get; init; }

    /// <summary>Releases the owning transport object (for example the QUIC connection) after the stream is disposed.</summary>
    public Func<ValueTask>? ReleaseAsync { get; init; }
}

/// <summary>
/// An <see cref="IWireConnection"/> over one duplex byte stream with <see cref="LengthPrefixFramer"/> framing: the shared
/// logic of <c>quic</c> and <c>webtransport</c>. Frame lengths are checked before payload bytes are buffered, the first
/// frame is capped at 8 KiB and 5 s, and a violation closes with 4400 (bad frame) or 4408 (hello timeout).
/// </summary>
public sealed class StreamWireConnection : IWireConnection
{
    /// <summary>Close code for a bad or oversized frame (doc 02).</summary>
    public const int BadFrame = 4400;

    /// <summary>Close code for a missing <c>hello</c> (doc 02).</summary>
    public const int HelloTimeout = 4408;

    private const int ReadChunk = 16 * 1024;

    private readonly Stream _stream;
    private readonly StreamWireConnectionOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    private byte[] _buffer = new byte[1024];
    private int _start;
    private int _end;
    private bool _helloSeen;
    private int _closed;
    private int _closeCode;

    /// <summary>Creates a connection; the preface handshake is already done.</summary>
    public StreamWireConnection(Stream stream, WireProfile profile, StreamWireConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        Profile = profile;
        _options = options ?? new StreamWireConnectionOptions();
        if (_options.MaxFrameBytes < 1 || _options.PreHelloMaxBytes < 1) throw new ArgumentOutOfRangeException(nameof(options));
    }

    /// <inheritdoc />
    public WireProfile Profile { get; }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        var writer = new ArrayBufferWriter<byte>(frame.Length + 8);
        LengthPrefixFramer.Write(frame.Span, writer, _options.MaxFrameBytes);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(writer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) != 0) return WireReceive.Closed(_closeCode);
        using var timeout = new CancellationTokenSource();
        if (!_helloSeen && _options.PreHelloTimeout is { } limit)
        {
            var left = limit - System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt);
            timeout.CancelAfter(left < TimeSpan.Zero ? TimeSpan.Zero : left);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            while (true)
            {
                var max = _helloSeen ? _options.MaxFrameBytes : Math.Min(_options.MaxFrameBytes, _options.PreHelloMaxBytes);
                var sequence = new ReadOnlySequence<byte>(_buffer, _start, _end - _start);
                bool got;
                ReadOnlySequence<byte> frame;
                try
                {
                    got = LengthPrefixFramer.TryRead(ref sequence, max, out frame);
                }
                catch (InvalidDataException)
                {
                    return await CloseWithAsync(BadFrame).ConfigureAwait(false);
                }

                if (got)
                {
                    var bytes = frame.ToArray();
                    _start = _end - (int)sequence.Length;
                    if (_start == _end) _start = _end = 0;
                    _helloSeen = true;
                    return WireReceive.Data(bytes);
                }

                Compact();
                if (_end == _buffer.Length) Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, max + 8 + ReadChunk));
                var read = await _stream.ReadAsync(_buffer.AsMemory(_end, Math.Min(_buffer.Length - _end, ReadChunk)), linked.Token).ConfigureAwait(false);
                if (read == 0) return await CloseWithAsync(0, signalPeer: false).ConfigureAwait(false);
                _end += read;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return await CloseWithAsync(HelloTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException || _options.CloseCodeFromException?.Invoke(ex) is not null)
        {
            var code = _options.CloseCodeFromException?.Invoke(ex) ?? 0;
            return await CloseWithAsync(code, signalPeer: false).ConfigureAwait(false);
        }
    }

    /// <summary>Closes the connection with a code the peer sees, then releases the stream.</summary>
    public async ValueTask CloseAsync(int code) => await CloseWithAsync(code).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseWithAsync(0).ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private void Compact()
    {
        if (_start == 0) return;
        Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
        _end -= _start;
        _start = 0;
    }

    private async ValueTask<WireReceive> CloseWithAsync(int code, bool signalPeer = true)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _closeCode = code;
            try
            {
                if (signalPeer && _options.CloseAsync is { } close) await close(code).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The peer is already gone; closing is best effort.
            }

            await _stream.DisposeAsync().ConfigureAwait(false);
            if (_options.ReleaseAsync is { } release) await release().ConfigureAwait(false);
        }

        return WireReceive.Closed(_closeCode);
    }
}
