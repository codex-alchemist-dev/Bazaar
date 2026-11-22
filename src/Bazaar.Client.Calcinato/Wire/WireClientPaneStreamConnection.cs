using System.Text;
using System.Threading.Channels;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Calcinato.Wire;

/// <summary>
/// <see cref="IPaneStreamConnection"/> over the socket <see cref="WireClient"/> (doc 55, section 6.1). Server to client, a
/// <c>pane.frame</c> event on <c>pane:&lt;id&gt;</c> carries <c>{"frame": "&lt;json text&gt;"}</c>; the frame text becomes the body.
/// Client to server, the body is sent as a <c>pane.frame</c> request on the stream, answered <c>ok</c> or an error, and the client's
/// request id makes a resend after a reconnect idempotent. Subscribe and unsubscribe are the connection's own messages.
/// </summary>
/// <remarks>
/// <see cref="Bodies"/> lives as long as the adapter, across reconnects of the client, so the hub's pump keeps running. The
/// client resubscribes the streams itself; <see cref="Reconnected"/> tells the head when to call
/// <see cref="PaneStreamTerminalWireHub.OnReconnectedAsync"/> so each terminal asks for a replay. The payload is the registered
/// <see cref="PaneFrame"/>, so every codec carries it.
/// </remarks>
public sealed class WireClientPaneStreamConnection : IPaneStreamConnection, IDisposable
{
    /// <summary>The message type of a terminal frame (doc 55, section 6.1).</summary>
    public const string PaneFrameType = WireTypes.PaneFrame;

    private const int BodyCapacity = 1024;

    private readonly WireClient _client;
    private readonly IWireCodec _codec;
    private readonly Channel<PaneStreamBody> _bodies = Channel.CreateBounded<PaneStreamBody>(
        new BoundedChannelOptions(BodyCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly IDisposable _sink;
    private int _connections;
    private int _disposed;

    /// <param name="client">The socket client; the adapter registers the <c>pane:</c> sink on it and does not own it.</param>
    /// <param name="codec">The codec the client uses, to read event payloads.</param>
    /// <exception cref="InvalidOperationException">The client already has a handler for <c>pane:</c>.</exception>
    public WireClientPaneStreamConnection(WireClient client, IWireCodec codec)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(codec);
        _client = client;
        _codec = codec;
        _sink = client.RegisterStreamSink(WireStreams.PanePrefix, OnEvent);
        client.Connected += OnConnected;
    }

    /// <summary>Raised on each connection after the first. The head calls the hub's <c>OnReconnectedAsync</c> from here (handing off, not awaiting inline).</summary>
    public event Action? Reconnected;

    /// <inheritdoc />
    public ChannelReader<PaneStreamBody> Bodies => _bodies.Reader;

    /// <inheritdoc />
    public async ValueTask SubscribeAsync(string stream, CancellationToken ct)
    {
        Check(stream);
        await _client.SubscribeAsync(stream, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask UnsubscribeAsync(string stream, CancellationToken ct)
    {
        Check(stream);
        await _client.UnsubscribeAsync(stream, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="WireRequestException">The server answered with an error (a <c>TerminalError</c> code).</exception>
    public async ValueTask SendAsync(string stream, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        Check(stream);
        await _client.SendRequestAsync(PaneFrameType, stream, new PaneFrame(Encoding.UTF8.GetString(body.Span)), ct).ConfigureAwait(false);
    }

    /// <summary>Removes the sink and completes <see cref="Bodies"/>. Does not touch the client.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.Connected -= OnConnected;
        _sink.Dispose();
        _bodies.Writer.TryComplete();
    }

    private static void Check(string stream)
    {
        if (!WireStreams.TryParseTerminal(stream, out _))
        {
            throw new ArgumentException("Not a pane stream name.", nameof(stream));
        }
    }

    private void OnConnected()
    {
        if (Interlocked.Increment(ref _connections) > 1) Reconnected?.Invoke();
    }

    private void OnEvent(Envelope env)
    {
        if (env.T != PaneFrameType || env.S is null || !WireStreams.TryParseTerminal(env.S, out _)) return;
        try
        {
            var frame = _codec.DecodePayload<PaneFrame>(env);
            _bodies.Writer.TryWrite(new PaneStreamBody(env.S, Encoding.UTF8.GetBytes(frame.Frame)));
        }
        catch (WireFormatException)
        {
            // A payload without a frame string is dropped; the connection stays up.
        }
    }
}
