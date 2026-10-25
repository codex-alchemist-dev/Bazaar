using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Wire.Connection;

/// <summary>
/// One socket from upgrade to close (doc 02, Connection and handshake, Heartbeats, Backpressure): hello and
/// welcome, authentication, a request loop with in-flight and rate limits, a bounded outbound queue, heartbeats
/// and credential expiry. Payloads are never logged.
/// </summary>
internal sealed partial class WireConnection : IWireConnection, IDisposable
{
    private const int _ServerMinor = 0;
    private const int _ServerMajor = 1;
    private const int _HeartbeatCloseCode = 1001;
    private const int _ServerFaultCloseCode = 1011;

    private readonly WebSocket _socket;
    private readonly string? _headerCredential;
    private readonly WireOptions _options;
    private readonly IReadOnlyList<IWireCodec> _codecs;
    private readonly IConnectionAuthenticator _authenticator;
    private readonly WireConnectionRegistry _registry;
    private readonly IdempotentRequestExecutor _idempotency;
    private readonly IReadOnlyDictionary<string, IWireRequestHandler> _handlers;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    // _cts stops this connection's own work (heartbeat, requests). _abort is the only token a pending receive sees:
    // cancelling a WebSocket receive aborts the socket, so it fires only to give up on a peer that will not finish the close handshake.
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly TaskCompletionSource _closeRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Outbound> _outbound = Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Task, byte> _requests = new();
    private readonly object _closeLock = new();

    private readonly record struct Outbound(byte[] Data, bool IsEvent);

    private IWireCodec _codec;
    private int _version = _ServerMajor;
    private (int Code, string Reason)? _closeRequest;
    private volatile bool _closing;
    private int _pendingEvents;
    private int _inflight;
    private long _lastActivityTicks;
    private DateTimeOffset? _expiresAt;
    private double _tokens;
    private long _tokensTicks;
    private int _limitedInARow;

    public WireConnection(
        WebSocket socket,
        string? headerCredential,
        WireOptions options,
        IReadOnlyList<IWireCodec> codecs,
        IConnectionAuthenticator authenticator,
        WireConnectionRegistry registry,
        IdempotentRequestExecutor idempotency,
        IReadOnlyDictionary<string, IWireRequestHandler> handlers,
        TimeProvider time,
        ILogger log)
    {
        _socket = socket;
        _headerCredential = headerCredential;
        _options = options;
        _codecs = codecs;
        _codec = codecs[0];
        _authenticator = authenticator;
        _registry = registry;
        _idempotency = idempotency;
        _handlers = handlers;
        _time = time;
        _log = log;
        _tokens = options.RequestBurst;
        _tokensTicks = time.GetUtcNow().UtcTicks;
    }

    public Guid ConnectionId { get; } = Guid.NewGuid();

    public PrincipalRef Principal { get; private set; } = new(Guid.Empty, "none");

    public string DeviceId { get; private set; } = string.Empty;

    public string DeviceClass { get; private set; } = string.Empty;

    public IWireCodec Codec => _codec;

    public IReadOnlyList<StreamCursor> HelloCursors { get; private set; } = [];

    public CancellationToken Closed => _cts.Token;

    public bool TryEnqueueEvent(Envelope envelope)
    {
        if (_cts.IsCancellationRequested || _closing) return false;
        if (Interlocked.Increment(ref _pendingEvents) > _options.OutboundQueueMax)
        {
            Interlocked.Decrement(ref _pendingEvents);
            return false;
        }

        if (!_outbound.Writer.TryWrite(new Outbound(EncodeFrame(envelope), IsEvent: true)))
        {
            Interlocked.Decrement(ref _pendingEvents);
            return false;
        }

        return true;
    }

    public bool TryEnqueueLagged(Lagged lagged) =>
        Enqueue(Frame(WireTypes.Lagged, EnvelopeKind.Event, null, lagged.S, null, _codec.EncodePayload(lagged)));

    public ValueTask CloseAsync(int closeCode, string reason)
    {
        lock (_closeLock)
        {
            _closeRequest ??= (closeCode, reason);
            _closing = true;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _closeRequested.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _cts.Dispose();
        _abort.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Wire connection {ConnectionId} failed.")]
    private static partial void LogConnectionFailed(ILogger log, Exception ex, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wire authentication failed: {Reason}")]
    private static partial void LogAuthFailed(ILogger log, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Request {Type} failed on {ConnectionId}.")]
    private static partial void LogRequestFailed(ILogger log, Exception ex, string type, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handler for {Type} failed.")]
    private static partial void LogHandlerFailed(ILogger log, Exception ex, string type);

    /// <summary>Runs the connection until it ends. Never throws for socket faults.</summary>
    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var abortReg = requestAborted.Register(() =>
        {
            _ = CloseAsync(_ServerFaultCloseCode, "aborted").AsTask();
            _abort.Cancel();
        });
        var sender = Task.CompletedTask;
        var receive = Task.CompletedTask;
        try
        {
            if (!await HandshakeAsync().ConfigureAwait(false))
            {
                return;
            }

            sender = Task.Run(() => SendLoopAsync(), CancellationToken.None);
            var heartbeat = Task.Run(() => HeartbeatLoopAsync(_cts.Token), CancellationToken.None);
            receive = ReceiveLoopAsync(_abort.Token);
            await Task.WhenAny(receive, _closeRequested.Task).ConfigureAwait(false);
            await CloseAsync(1000, "closed").ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            await CloseAsync(_ServerFaultCloseCode, "socket fault").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogConnectionFailed(_log, ex, ConnectionId);
            await CloseAsync(_ServerFaultCloseCode, "internal").ConfigureAwait(false);
        }
        finally
        {
            _registry.Remove(this);
            _outbound.Writer.TryComplete();
            await DrainAsync(sender).ConfigureAwait(false);
            await FinalCloseAsync(receive).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- handshake

    private async Task<bool> HandshakeAsync()
    {
        using var helloTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.HelloTimeoutS), _time);
        using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(helloTimeout.Token, _abort.Token);

        Envelope hello;
        Hello body;
        var pending = ReceiveFrameAsync(_abort.Token);
        var timedOut = Task.Delay(Timeout.InfiniteTimeSpan, helloTimeout.Token);
        try
        {
            if (await Task.WhenAny(pending, timedOut).ConfigureAwait(false) != pending)
            {
                await CloseDirectAsync(WireCloseCodes.HelloTimeout, "hello timeout").ConfigureAwait(false);
                await AwaitPeerAsync(pending).ConfigureAwait(false);
                return false;
            }

            var frame = await pending.ConfigureAwait(false);
            if (frame is null) return false;
            hello = _codec.Decode(frame.Value.Span);
            if (hello.T != WireTypes.Hello || hello.K != EnvelopeKind.Request) throw new WireFormatException("First frame must be hello.");
            body = _codec.DecodePayload<Hello>(hello);
            if (string.IsNullOrWhiteSpace(body.DeviceId) || body.DeviceId.Length > 128) throw new WireFormatException("Bad device id.");
        }
        catch (WireFormatException)
        {
            await CloseDirectAsync(WireCloseCodes.BadFrame, "bad frame").ConfigureAwait(false);
            return false;
        }

        // Version and codec first, so an old client learns it must upgrade whatever its credential.
        var serverMin = _options.PreviousMajorSupported ? _ServerMajor - 1 : _ServerMajor;
        var chosen = Math.Min(body.VersionMax, _ServerMajor);
        var codec = _codecs.FirstOrDefault(c => body.Codecs.Contains(c.Name, StringComparer.Ordinal));
        if (chosen < Math.Max(body.VersionMin, serverMin) || codec is null)
        {
            var upgrade = new Welcome(
                _ServerMajor, _ServerMinor, _codec.Name, Guid.Empty, _time.GetUtcNow(), _options.HeartbeatIntervalS,
                _options.HeartbeatTimeoutS, Limits(), [], 0, true, _options.MinimumBuild);
            await SendDirectAsync(Frame(WireTypes.Welcome, EnvelopeKind.Response, hello.Id, null, _time.GetUtcNow(), _codec.EncodePayload(upgrade))).ConfigureAwait(false);
            await CloseDirectAsync(WireCloseCodes.UpgradeRequired, "upgrade required").ConfigureAwait(false);
            return false;
        }

        _version = chosen;
        _codec = codec;

        AuthResult auth;
        try
        {
            var credential = !string.IsNullOrEmpty(body.Credential) ? body.Credential : _headerCredential;
            auth = await _authenticator.AuthenticateAsync(
                string.IsNullOrEmpty(credential) ? null : new HelloCredential(credential),
                new DeviceInfo(body.DeviceId, body.DeviceClass, body.ClientBuild),
                helloCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (helloTimeout.IsCancellationRequested)
        {
            await CloseDirectAsync(WireCloseCodes.HelloTimeout, "hello timeout").ConfigureAwait(false);
            return false;
        }

        if (!auth.Ok || auth.Principal is null)
        {
            LogAuthFailed(_log, auth.Reason ?? "unspecified");
            await CloseDirectAsync(WireCloseCodes.AuthFailed, "authentication failed").ConfigureAwait(false);
            return false;
        }

        Principal = auth.Principal;
        DeviceId = auth.DeviceId ?? body.DeviceId;
        DeviceClass = body.DeviceClass;
        HelloCursors = body.Cursors ?? [];
        _expiresAt = auth.ExpiresAt;
        if (_expiresAt is { } exp && exp <= _time.GetUtcNow())
        {
            await CloseDirectAsync(WireCloseCodes.AuthFailed, "credential expired").ConfigureAwait(false);
            return false;
        }

        Touch();
        foreach (var evicted in _registry.Add(this, _options.MaxSocketsPerDevice))
        {
            await evicted.CloseAsync(1008, "superseded by a newer socket").ConfigureAwait(false);
        }

        var welcome = new Welcome(
            _version, _ServerMinor, _codec.Name, Principal.Id, _time.GetUtcNow(), _options.HeartbeatIntervalS,
            _options.HeartbeatTimeoutS, Limits(), _options.Capabilities.ToArray(), 0, null, null);
        await SendDirectAsync(Frame(WireTypes.Welcome, EnvelopeKind.Response, hello.Id, null, _time.GetUtcNow(), _codec.EncodePayload(welcome))).ConfigureAwait(false);
        return true;
    }

    private WireLimits Limits() => new(_options.MaxFrameBytes, _options.InflightRequestsMax, _options.ReplayPageSize);

    // ---------------------------------------------------------------- receive

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Memory<byte>? frame;
            try
            {
                frame = await ReceiveFrameAsync(ct).ConfigureAwait(false);
            }
            catch (WireFormatException)
            {
                await CloseAsync(WireCloseCodes.BadFrame, "bad frame").ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                return;
            }

            if (frame is null) return;
            Touch();

            Envelope envelope;
            try
            {
                envelope = _codec.Decode(frame.Value.Span);
            }
            catch (WireFormatException)
            {
                await CloseAsync(WireCloseCodes.BadFrame, "bad frame").ConfigureAwait(false);
                return;
            }

            if (envelope.K == EnvelopeKind.Request)
            {
                await AcceptRequestAsync(envelope).ConfigureAwait(false);
            }

            // Events, responses and errors from a client (such as pong) only prove liveness.
        }
    }

    private async ValueTask AcceptRequestAsync(Envelope request)
    {
        if (string.IsNullOrEmpty(request.Id) || request.Id.Length > 128)
        {
            Reply(request, WireReply.Fail(WireErrorCodes.BadRequest, "A request needs an id of at most 128 characters."));
            return;
        }

        if (!TakeToken())
        {
            await LimitedAsync(request, 1000 / Math.Max(1, _options.RequestsPerSecond)).ConfigureAwait(false);
            return;
        }

        if (Interlocked.Increment(ref _inflight) > _options.InflightRequestsMax)
        {
            Interlocked.Decrement(ref _inflight);
            await LimitedAsync(request, 100).ConfigureAwait(false);
            return;
        }

        _limitedInARow = 0;
        var task = Task.Run(async () =>
        {
            try
            {
                var reply = await _idempotency.ExecuteAsync(DeviceId, request.Id, token => RunHandlerAsync(request, token), _cts.Token).ConfigureAwait(false);
                Reply(request, reply);
            }
            catch (OperationCanceledException)
            {
                // Connection closed; the client retries with the same id.
            }
            catch (Exception ex)
            {
                LogRequestFailed(_log, ex, request.T, ConnectionId);
                Reply(request, WireReply.Fail(WireErrorCodes.Internal));
            }
            finally
            {
                Interlocked.Decrement(ref _inflight);
            }
        }, CancellationToken.None);
        _requests[task] = 0;
        _ = task.ContinueWith(t => _requests.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async ValueTask<WireReply> RunHandlerAsync(Envelope request, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(request.T, out var handler))
        {
            return WireReply.Fail(WireErrorCodes.Unsupported, $"Unknown request '{request.T}'.");
        }

        try
        {
            return await handler.HandleAsync(new WireRequestContext(this, request), ct).ConfigureAwait(false);
        }
        catch (WireFormatException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The payload does not match the request type.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_log, ex, request.T);
            return WireReply.Fail(WireErrorCodes.Internal);
        }
    }

    private async ValueTask LimitedAsync(Envelope request, int retryAfterMs)
    {
        Reply(request, WireReply.Fail(WireErrorCodes.RateLimited, "Too many requests.", retryAfterMs));
        if (++_limitedInARow >= _options.RateLimitCloseAfter)
        {
            await CloseAsync(WireCloseCodes.RateLimited, "rate limited").ConfigureAwait(false);
        }
    }

    private bool TakeToken()
    {
        var now = _time.GetUtcNow().UtcTicks;
        var seconds = (now - _tokensTicks) / (double)TimeSpan.TicksPerSecond;
        _tokensTicks = now;
        _tokens = Math.Min(_options.RequestBurst, _tokens + (seconds * _options.RequestsPerSecond));
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }

    /// <summary>Reads one frame. Null when the peer closed. Throws <see cref="WireFormatException"/> for oversize or wrong-type frames.</summary>
    private async Task<Memory<byte>?> ReceiveFrameAsync(CancellationToken ct)
    {
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var buffer = writer.GetMemory(4096);
            var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType == WebSocketMessageType.Binary != _codec.IsBinary)
            {
                throw new WireFormatException("Frame type does not match the codec.");
            }

            writer.Advance(result.Count);
            if (writer.WrittenCount > _options.MaxFrameBytes) throw new WireFormatException("Frame is too large.");
            if (result.EndOfMessage) return MemoryMarshalAsMemory(writer);
        }
    }

    private static Memory<byte> MemoryMarshalAsMemory(ArrayBufferWriter<byte> writer) => writer.WrittenMemory.ToArray();

    // ---------------------------------------------------------------- send

    private void Reply(Envelope request, WireReply reply)
    {
        var frame = reply.IsOk
            ? Frame(request.T, EnvelopeKind.Response, request.Id, request.S, _time.GetUtcNow(), reply.Payload)
            : Frame(request.T, EnvelopeKind.Error, request.Id, request.S, _time.GetUtcNow(), _codec.EncodePayload(reply.Error!));
        Enqueue(frame);
    }

    private Envelope Frame(string type, EnvelopeKind kind, string? re, string? stream, DateTimeOffset? ts, ReadOnlyMemory<byte> payload) =>
        new(_version, type, kind, null, re, stream, null, ts, payload);

    private bool Enqueue(Envelope envelope) =>
        !_closing && _outbound.Writer.TryWrite(new Outbound(EncodeFrame(envelope), IsEvent: false));

    private byte[] EncodeFrame(in Envelope envelope)
    {
        var writer = new ArrayBufferWriter<byte>();
        _codec.Encode(envelope, writer);
        return writer.WrittenSpan.ToArray();
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var item in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (item.IsEvent) Interlocked.Decrement(ref _pendingEvents);
                await _socket.SendAsync(
                    item.Data,
                    _codec.IsBinary ? WebSocketMessageType.Binary : WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            await CloseAsync(_ServerFaultCloseCode, "send failed").ConfigureAwait(false);
        }
    }

    private async Task SendDirectAsync(Envelope envelope)
    {
        var writer = new ArrayBufferWriter<byte>();
        _codec.Encode(envelope, writer);
        await _socket.SendAsync(
            writer.WrittenMemory,
            _codec.IsBinary ? WebSocketMessageType.Binary : WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- heartbeat, expiry

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var tick = TimeSpan.FromSeconds(Math.Min(1, _options.HeartbeatIntervalS));
        var interval = TimeSpan.FromSeconds(_options.HeartbeatIntervalS);
        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutS);
        var lastPing = _time.GetUtcNow();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(tick, _time, ct).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                if (_expiresAt is { } exp && exp <= now)
                {
                    await CloseAsync(WireCloseCodes.AuthFailed, "credential expired").ConfigureAwait(false);
                    return;
                }

                if (now - new DateTimeOffset(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero) >= timeout)
                {
                    await CloseAsync(_HeartbeatCloseCode, "heartbeat timeout").ConfigureAwait(false);
                    return;
                }

                if (now - lastPing >= interval)
                {
                    lastPing = now;
                    Enqueue(Frame(WireTypes.Ping, EnvelopeKind.Event, null, null, now, _codec.EncodePayload(new Ping(null))));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, _time.GetUtcNow().UtcTicks);

    // ---------------------------------------------------------------- close

    private async Task DrainAsync(Task sender)
    {
        foreach (var pending in _requests.Keys.ToArray())
        {
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        try
        {
            await sender.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    private async Task FinalCloseAsync(Task pendingReceive)
    {
        (int Code, string Reason) close;
        lock (_closeLock)
        {
            close = _closeRequest ?? (1000, "closed");
        }

        await CloseDirectAsync(close.Code, close.Reason).ConfigureAwait(false);
        await AwaitPeerAsync(pendingReceive).ConfigureAwait(false);
    }

    /// <summary>Waits a moment for the peer to answer our close frame; a peer that does not is dropped.</summary>
    private async Task AwaitPeerAsync(Task pendingReceive)
    {
        try
        {
            await pendingReceive.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            _abort.Cancel();
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            await pendingReceive.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
        }
    }

    private async Task CloseDirectAsync(int code, string reason)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
    }
}
