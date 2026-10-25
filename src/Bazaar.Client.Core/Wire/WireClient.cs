using System.Buffers;
using System.Threading.Channels;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Core.Wire;

/// <summary>
/// Client side of the socket protocol (docs/design/02-wire-protocol-and-sync.md): hello/welcome, subscribe with
/// cursors, in-order delivery with de-duplication by <c>(n, ev)</c>, <c>lagged</c> and <c>resync</c> handling,
/// idempotent request retry, and reconnect with jittered backoff. No UI and no sockets; see <see cref="IWireTransportFactory"/>.
/// </summary>
public sealed class WireClient(
    IWireTransportFactory transport,
    IWireCodec codec,
    ICursorStore cursors,
    WireClientOptions options,
    TimeProvider time)
{
    private readonly Channel<WireGcMessage> _messages = Channel.CreateUnbounded<WireGcMessage>();
    private readonly Channel<Resync> _resyncs = Channel.CreateUnbounded<Resync>();
    private readonly Lock _gate = new();
    private readonly HashSet<string> _streams = [];
    private readonly Dictionary<string, Pending> _pending = [];
    private IWireConnection? _connection;
    private int _nextId;
    private DateTimeOffset _lastFrame;

    /// <summary>Messages in the order applied; duplicates already removed.</summary>
    public ChannelReader<WireGcMessage> Messages => _messages.Reader;

    /// <summary>Resync notices: the client should page back with <c>gc.history</c>.</summary>
    public ChannelReader<Resync> Resyncs => _resyncs.Reader;

    /// <summary>Set once the client has stopped for good; null while it runs.</summary>
    public WireClientStopReason? Stopped { get; private set; }

    /// <summary>Number of connections opened so far.</summary>
    public int ConnectionCount { get; private set; }

    /// <summary>Subscribes to a stream now (if connected) and on every reconnect, resuming from its cursor.</summary>
    public async Task SubscribeAsync(string stream, CancellationToken cancellationToken = default)
    {
        IWireConnection? conn;
        lock (_gate)
        {
            _streams.Add(stream);
            conn = _connection;
        }

        if (conn is not null) await SendSubscribeAsync(conn, [stream], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request and waits for its answer. Unanswered requests are resent with the same id after a reconnect,
    /// which the server answers once (doc 02, idempotency).
    /// </summary>
    /// <exception cref="WireRequestException">The server answered with an error.</exception>
    public async Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
    {
        string id = $"{options.DeviceId}-{Interlocked.Increment(ref _nextId)}";
        var env = new Envelope(options.ProtocolMajor, type, EnvelopeKind.Request, id, null, null, null, null, codec.EncodePayload(payload));
        var pending = new Pending(Frame(env));
        IWireConnection? conn;
        lock (_gate)
        {
            _pending[id] = pending;
            conn = _connection;
        }

        using var reg = cancellationToken.Register(() => pending.Completion.TrySetCanceled(cancellationToken));
        try
        {
            if (conn is not null) await TrySendAsync(conn, pending.Frame, cancellationToken).ConfigureAwait(false);
            var answer = await pending.Completion.Task.ConfigureAwait(false);
            if (answer.K == EnvelopeKind.Error) throw new WireRequestException(codec.DecodePayload<WireError>(answer));
            return codec.DecodePayload<TResponse>(answer);
        }
        finally
        {
            lock (_gate) _pending.Remove(id);
        }
    }

    /// <summary>Connects and stays connected until cancelled or a close code ends the session for good.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int attempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int? terminal = null;
                try
                {
                    var conn = await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    await using (conn.ConfigureAwait(false))
                    {
                        ConnectionCount++;
                        terminal = await RunConnectionAsync(conn, () => attempt = 0, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not WireFormatException)
                {
                    // Unreachable server or a broken socket: back off and retry.
                }
                catch (WireFormatException)
                {
                    // A bad frame closes the socket (4400); reconnect like any other drop.
                }
                finally
                {
                    lock (_gate) _connection = null;
                }

                if (Terminal(terminal) is { } reason)
                {
                    Stopped = reason;
                    return;
                }

                await Task.Delay(BackoffDelay(attempt++), time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        Stopped ??= WireClientStopReason.Cancelled;
        _messages.Writer.TryComplete();
        _resyncs.Writer.TryComplete();
    }

    private static WireClientStopReason? Terminal(int? closeCode) => closeCode switch
    {
        WireCloseCodes.UpgradeRequired => WireClientStopReason.UpgradeRequired,
        WireCloseCodes.AuthFailed => WireClientStopReason.AuthFailed,
        WireCloseCodes.Revoked => WireClientStopReason.Revoked,
        _ => null,
    };

    /// <summary>The delay before reconnect attempt <paramref name="attempt"/> (0-based): doubling, capped, with jitter.</summary>
    public TimeSpan BackoffDelay(int attempt)
    {
        double seconds = options.BackoffStart.TotalSeconds * Math.Pow(2, Math.Min(attempt, 20));
        seconds = Math.Min(seconds, options.BackoffMax.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * (0.5 + 0.5 * options.Jitter()));
    }

    /// <returns>The close code when the server ended the socket, or null when it dropped without one.</returns>
    private async Task<int?> RunConnectionAsync(IWireConnection conn, Action connected, CancellationToken ct)
    {
        await SendAsync(conn, new Envelope(options.ProtocolMajor, WireTypes.Hello, EnvelopeKind.Request, null, null, null, null, null,
            codec.EncodePayload(new Hello(options.ProtocolMajor, options.ProtocolMajor, options.ProtocolMinor, [codec.Name],
                options.DeviceId, options.DeviceClass, options.ClientBuild, options.Credential, null))), ct).ConfigureAwait(false);

        var first = await conn.ReceiveAsync(ct).ConfigureAwait(false);
        if (first.IsClosed) return first.CloseCode;
        var welcomeEnv = codec.Decode(first.Frame.Span);
        if (welcomeEnv.T != WireTypes.Welcome) throw new WireFormatException("Expected welcome.");
        var welcome = codec.DecodePayload<Welcome>(welcomeEnv);
        if (welcome.UpgradeRequired == true) return WireCloseCodes.UpgradeRequired;
        connected();

        string[] streams;
        List<Pending> resend;
        lock (_gate)
        {
            _connection = conn;
            streams = [.. _streams];
            resend = [.. _pending.Values];
        }

        if (streams.Length > 0) await SendSubscribeAsync(conn, streams, ct).ConfigureAwait(false);
        foreach (var p in resend) await conn.SendAsync(p.Frame, ct).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(conn, welcome, linked);
        try
        {
            while (true)
            {
                var received = await conn.ReceiveAsync(linked.Token).ConfigureAwait(false);
                if (received.IsClosed) return received.CloseCode == 0 ? null : received.CloseCode;
                _lastFrame = time.GetUtcNow();
                await HandleAsync(codec.Decode(received.Frame.Span), conn, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // heartbeat timeout: treat as a drop
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Pings at the server's interval and cancels the connection after the server's silence limit.</summary>
    private async Task HeartbeatAsync(IWireConnection conn, Welcome welcome, CancellationTokenSource linked)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, welcome.HeartbeatIntervalS));
        var timeout = TimeSpan.FromSeconds(Math.Max(1, welcome.HeartbeatTimeoutS));
        _lastFrame = time.GetUtcNow();
        while (!linked.IsCancellationRequested)
        {
            await Task.Delay(interval, time, linked.Token).ConfigureAwait(false);
            if (time.GetUtcNow() - _lastFrame >= timeout)
            {
                await linked.CancelAsync().ConfigureAwait(false);
                return;
            }

            await TrySendAsync(conn, Frame(new Envelope(options.ProtocolMajor, WireTypes.Ping, EnvelopeKind.Request, null, null, null, null, null,
                codec.EncodePayload(new Ping(null)))), linked.Token).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleAsync(Envelope env, IWireConnection conn, CancellationToken ct)
    {
        switch (env.K, env.T)
        {
            case (EnvelopeKind.Response or EnvelopeKind.Error, _) when env.Re is not null:
                Pending? p;
                lock (_gate) _pending.TryGetValue(env.Re, out p);
                p?.Completion.TrySetResult(env);
                break;
            case (EnvelopeKind.Event, WireTypes.GcMessage):
                Apply(codec.DecodePayload<WireGcMessage>(env));
                break;
            case (EnvelopeKind.Event, WireTypes.Lagged):
                // Live delivery stopped for this stream; resubscribe from the cursor so nothing is lost.
                await SendSubscribeAsync(conn, [codec.DecodePayload<Lagged>(env).S], ct).ConfigureAwait(false);
                break;
            case (EnvelopeKind.Event, WireTypes.Resync):
                var resync = codec.DecodePayload<Resync>(env);
                foreach (var m in resync.Tail) Apply(m);
                var cur = cursors.Load(resync.S);
                cursors.Save(new StreamCursor(resync.S, Math.Max(cur.After, resync.LatestN), Math.Max(cur.EditAfter, resync.LatestEv)));
                _resyncs.Writer.TryWrite(resync);
                break;
            case (EnvelopeKind.Event, WireTypes.StreamRevoked):
                lock (_gate) _streams.Remove(codec.DecodePayload<StreamRevoked>(env).S);
                break;
            default:
                break; // unknown events are ignored (minor versions add them)
        }
    }

    /// <summary>Delivers a message unless this <c>(n, ev)</c> was already applied.</summary>
    private void Apply(WireGcMessage m)
    {
        string stream = WireStreams.Gc(m.GcId);
        var cur = cursors.Load(stream);
        if (m.N <= cur.After && m.Ev <= cur.EditAfter) return;
        cursors.Save(new StreamCursor(stream, Math.Max(cur.After, m.N), Math.Max(cur.EditAfter, m.Ev)));
        _messages.Writer.TryWrite(m);
    }

    private ValueTask SendSubscribeAsync(IWireConnection conn, IEnumerable<string> streams, CancellationToken ct)
    {
        var sub = new Subscribe([.. streams.Select(cursors.Load)]);
        return TrySendAsync(conn, Frame(new Envelope(options.ProtocolMajor, WireTypes.Subscribe, EnvelopeKind.Request,
            $"{options.DeviceId}-{Interlocked.Increment(ref _nextId)}", null, null, null, null, codec.EncodePayload(sub))), ct);
    }

    /// <summary>Sends, ignoring a failure: a dead socket ends the receive loop, which reconnects and resends.</summary>
    private static async ValueTask TrySendAsync(IWireConnection conn, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        try
        {
            await conn.SendAsync(frame, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    private ValueTask SendAsync(IWireConnection conn, Envelope env, CancellationToken ct) => conn.SendAsync(Frame(env), ct);

    private byte[] Frame(Envelope env)
    {
        var w = new ArrayBufferWriter<byte>();
        codec.Encode(env, w);
        return w.WrittenSpan.ToArray();
    }

    private sealed class Pending(byte[] frame)
    {
        public byte[] Frame { get; } = frame;

        public TaskCompletionSource<Envelope> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
