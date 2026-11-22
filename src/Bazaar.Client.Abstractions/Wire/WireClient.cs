using System.Buffers;
using System.Threading.Channels;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Abstractions.Wire;

/// <summary>Handler for an event on a stream family registered with <see cref="WireClient.RegisterStreamSink"/>.</summary>
/// <param name="envelope">The event envelope: type <c>t</c>, stream <c>s</c> and the still-encoded payload <c>p</c>.</param>
public delegate void WireStreamSink(Envelope envelope);

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
    TimeProvider time) : IWireRequester
{
    private readonly Channel<WireGcMessage> _messages = Channel.CreateUnbounded<WireGcMessage>();
    private readonly Channel<Resync> _resyncs = Channel.CreateUnbounded<Resync>();
    private readonly Lock _gate = new();
    private readonly HashSet<string> _streams = [];
    private readonly Dictionary<string, Pending> _pending = [];
    private readonly Dictionary<string, WireStreamSink> _sinks = [];
    private IWireConnection? _connection;
    private readonly string _idPrefix = Guid.NewGuid().ToString("N");
    private int _nextId;
    private DateTimeOffset _lastFrame;

    /// <summary>Messages in the order applied; duplicates already removed.</summary>
    public ChannelReader<WireGcMessage> Messages => _messages.Reader;

    /// <summary>Resync notices: the client should page back with <c>gc.history</c>.</summary>
    public ChannelReader<Resync> Resyncs => _resyncs.Reader;

    /// <summary>
    /// Raised on the receive loop for each live <c>gc.reaction</c> event (docs/design/02, "Reactions on the wire"). Handlers must not
    /// block or await a request inline; the receive loop is the thing that answers requests.
    /// </summary>
    public event Action<WireGcReaction>? ReactionReceived;

    /// <summary>Raised on the receive loop for each typing delta (<c>presence.update</c> of kind <c>typing</c>). The same rules apply to handlers.</summary>
    public event Action<PresenceTyping>? TypingReceived;

    /// <summary>Raised on the receive loop for each agent presence delta (<c>presence.update</c> of kind <c>agent</c>). The same rules apply to handlers.</summary>
    public event Action<PresenceUpdate>? PresenceUpdateReceived;

    /// <summary>
    /// Raised on the receive loop after each connection is established and its streams are subscribed, the first one included. State
    /// that is not replayed (reactions) is fetched again here. Handlers must hand the work off, not await a request inline.
    /// </summary>
    public event Action? Connected;

    /// <summary>The principal the server authenticated this client as, from the latest <c>welcome</c>; null before the first one.</summary>
    public Guid? PrincipalId { get; private set; }

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

    /// <summary>Stops a stream: it is no longer resubscribed on reconnect, and the server is told when connected.</summary>
    public async Task UnsubscribeAsync(string stream, CancellationToken cancellationToken = default)
    {
        IWireConnection? conn;
        lock (_gate)
        {
            _streams.Remove(stream);
            conn = _connection;
        }

        if (conn is null) return;
        var unsubscribe = new Unsubscribe([stream]);
        await TrySendAsync(conn, Frame(new Envelope(options.ProtocolMajor, WireTypes.Unsubscribe, EnvelopeKind.Request,
            NextId(), null, null, null, null, codec.EncodePayload(unsubscribe))), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers the one handler for events on streams that start with <paramref name="prefix"/> (for example <c>pane:</c>), for
    /// features that own a stream family outside this class. Only events this client has no built-in handling for reach a sink, so a
    /// sink cannot take over <c>gc.message</c> and the other built-in types. Streams with no sink are ignored as before.
    /// </summary>
    /// <param name="prefix">The stream prefix, ending in the separator.</param>
    /// <param name="sink">Raised on the receive loop. It must not block or await a request inline.</param>
    /// <returns>A handle that removes the registration.</returns>
    /// <exception cref="InvalidOperationException">The prefix already has a handler.</exception>
    public IDisposable RegisterStreamSink(string prefix, WireStreamSink sink)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            if (!_sinks.TryAdd(prefix, sink)) throw new InvalidOperationException($"Stream prefix '{prefix}' already has a handler.");
        }

        return new SinkRegistration(this, prefix, sink);
    }

    /// <summary>
    /// Sends a request and waits for its answer. Unanswered requests are resent with the same id after a reconnect,
    /// which the server answers once (doc 02, idempotency).
    /// </summary>
    /// <exception cref="WireRequestException">The server answered with an error.</exception>
    public async Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
    {
        var answer = await RequestCoreAsync(type, null, codec.EncodePayload(payload), cancellationToken).ConfigureAwait(false);
        return codec.DecodePayload<TResponse>(answer);
    }

    /// <summary>
    /// Sends a request whose payload the caller already encoded in this client's codec, optionally on a stream, and waits for the
    /// <c>ok</c> (the answer's payload, if any, is not read). For features that own their payload type outside the registered
    /// wire payloads. Resend and idempotency are as for <see cref="RequestAsync{TRequest, TResponse}"/>.
    /// </summary>
    /// <exception cref="WireRequestException">The server answered with an error.</exception>
    public async Task SendRawRequestAsync(string type, string? stream, ReadOnlyMemory<byte> encodedPayload, CancellationToken cancellationToken = default) =>
        await RequestCoreAsync(type, stream, encodedPayload, cancellationToken).ConfigureAwait(false);

    private async Task<Envelope> RequestCoreAsync(string type, string? stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        string id = NextId();
        var env = new Envelope(options.ProtocolMajor, type, EnvelopeKind.Request, id, null, stream, null, null, payload);
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
            return answer;
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
        try
        {
            options.OnWelcome?.Invoke(welcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A faulty observer must not break the connection.
        }

        if (welcome.UpgradeRequired == true) return WireCloseCodes.UpgradeRequired;
        PrincipalId = welcome.PrincipalId;
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
        Connected?.Invoke();

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
            case (EnvelopeKind.Event, WireTypes.GcReaction):
                ReactionReceived?.Invoke(codec.DecodePayload<WireGcReaction>(env));
                break;
            case (EnvelopeKind.Event, WireTypes.PresenceUpdate):
                var presence = codec.DecodePayload<PresenceTyping>(env);
                // Other kinds of presence delta share this type; typing and agent rows are read here, the rest are ignored.
                if (presence.Kind == PresenceKinds.Typing) TypingReceived?.Invoke(presence);
                else if (presence.Kind == PresenceKinds.Agent) PresenceUpdateReceived?.Invoke(codec.DecodePayload<PresenceUpdate>(env));
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
                DispatchToSink(env); // other events go to a registered stream sink; with none they are ignored (minor versions add them)
                break;
        }
    }

    private void DispatchToSink(Envelope env)
    {
        if (env.K != EnvelopeKind.Event || env.S is null) return;
        WireStreamSink? sink = null;
        lock (_gate)
        {
            foreach (var (prefix, candidate) in _sinks)
            {
                if (env.S.StartsWith(prefix, StringComparison.Ordinal))
                {
                    sink = candidate;
                    break;
                }
            }
        }

        if (sink is null) return;
        try
        {
            sink(env);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A faulty sink must not break the connection.
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
            NextId(), null, null, null, null, codec.EncodePayload(sub))), ct);
    }

    /// <summary>
    /// A request id unique to this client instance. The server keeps answers per (device, id) for a day, so ids must not
    /// restart at 1 in a new process that reuses the device id.
    /// </summary>
    private string NextId() => $"{_idPrefix}-{Interlocked.Increment(ref _nextId)}";

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

    private sealed class SinkRegistration(WireClient owner, string prefix, WireStreamSink sink) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (owner._sinks.TryGetValue(prefix, out var current) && current == sink) owner._sinks.Remove(prefix);
            }
        }
    }

    private sealed class Pending(byte[] frame)
    {
        public byte[] Frame { get; } = frame;

        public TaskCompletionSource<Envelope> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
