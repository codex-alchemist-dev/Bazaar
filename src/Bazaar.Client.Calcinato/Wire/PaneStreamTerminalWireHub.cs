using System.Threading.Channels;
using Bazaar.Client.Calcinato.Sources;
using Bazaar.Contracts.Terminal;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Calcinato.Wire;

/// <summary>
/// Multiplexes the <c>pane:&lt;terminalId&gt;</c> streams of one connection into per-terminal <see cref="ITerminalWire"/>
/// views (doc 55, section 6). Each terminal subscribes lazily on its first send and unsubscribes when released. Bodies
/// that do not decode, or that decode to another terminal id, are dropped and counted in <see cref="DroppedFrames"/>;
/// they never throw. Every terminal has a bounded incoming channel that drops its oldest frame on overflow.
/// </summary>
/// <remarks>
/// The hub does not own the connection and does not dispose it. It owns its pump task, which ends when the hub is
/// disposed or when the connection's body reader completes. Pump restarts happen in <see cref="OnReconnectedAsync"/>.
/// </remarks>
public sealed class PaneStreamTerminalWireHub : ITerminalWireHub, IAsyncDisposable
{
    private readonly IPaneStreamConnection _connection;
    private readonly int _capacity;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PaneStreamTerminalWire> _wires = [];
    private readonly CancellationTokenSource _pumpCts = new();
    private Task _pump = Task.CompletedTask;
    private long _droppedFrames;
    private bool _disposed;

    /// <summary>Starts the hub's pump over the connection's body reader.</summary>
    /// <param name="connection">The connection that carries the pane streams. The hub does not dispose it.</param>
    /// <param name="capacity">Frames buffered per terminal before the oldest is dropped.</param>
    public PaneStreamTerminalWireHub(IPaneStreamConnection connection, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _connection = connection;
        _capacity = capacity;
        _pump = PumpAsync(_pumpCts.Token);
    }

    /// <summary>Bodies dropped because they did not decode or decoded to a frame for another terminal.</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid terminal id.</exception>
    /// <exception cref="ObjectDisposedException">The hub is disposed.</exception>
    public ITerminalWire ForTerminal(string id)
    {
        var stream = WireStreams.Terminal(id);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_wires.TryGetValue(id, out var wire))
            {
                wire = new PaneStreamTerminalWire(_connection, id, stream, _capacity, Release);
                _wires[id] = wire;
            }

            return wire;
        }
    }

    /// <summary>Disposes the wire for one terminal id, which unsubscribes its stream. A later <see cref="ForTerminal"/> gives a new wire.</summary>
    public async ValueTask ReleaseAsync(string id)
    {
        PaneStreamTerminalWire? wire;
        lock (_gate)
        {
            _wires.TryGetValue(id, out wire);
        }

        if (wire is not null)
        {
            await wire.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Call after the connection is back. Restarts the pump if it ended and re-subscribes every active terminal. Each
    /// terminal whose incoming channel closed gets a fresh one. The terminal sources then request replay themselves.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The hub is disposed.</exception>
    public async ValueTask OnReconnectedAsync(CancellationToken ct = default)
    {
        PaneStreamTerminalWire[] live;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pump.IsCompleted)
            {
                _pump = PumpAsync(_pumpCts.Token);
            }

            live = [.. _wires.Values];
        }

        foreach (var wire in live)
        {
            await wire.ReopenAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>Disposes every wire (unsubscribing each stream), then stops and awaits the pump.</remarks>
    public async ValueTask DisposeAsync()
    {
        PaneStreamTerminalWire[] all;
        Task pump;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = [.. _wires.Values];
            _wires.Clear();
            pump = _pump;
        }

        foreach (var wire in all)
        {
            await wire.DisposeAsync().ConfigureAwait(false);
        }

        await _pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by disposal.
        }

        _pumpCts.Dispose();
    }

    private void Release(PaneStreamTerminalWire wire)
    {
        lock (_gate)
        {
            if (_wires.TryGetValue(wire.TerminalId, out var current) && ReferenceEquals(current, wire))
            {
                _wires.Remove(wire.TerminalId);
            }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var body in _connection.Bodies.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Route(body);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposal stops the pump.
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The connection's reader failed. Terminals see their incoming close, and a reconnect restarts the pump.
        }
        finally
        {
            CloseAll();
        }
    }

    private void Route(PaneStreamBody body)
    {
        if (!WireStreams.TryParseTerminal(body.Stream, out var id))
        {
            return;
        }

        var decoded = TerminalFrameCodec.Decode(body.Body.Span);
        if (decoded.Frame is not { } frame || frame.TerminalId != id)
        {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }

        PaneStreamTerminalWire? wire;
        lock (_gate)
        {
            _wires.TryGetValue(id, out wire);
        }

        wire?.Deliver(frame);
    }

    private void CloseAll()
    {
        PaneStreamTerminalWire[] live;
        lock (_gate)
        {
            live = [.. _wires.Values];
        }

        foreach (var wire in live)
        {
            wire.Disconnect();
        }
    }
}

/// <summary>
/// One terminal's view of the hub. Subscribes on its first send, completes <see cref="Incoming"/> when the connection
/// closes or the wire is disposed, and gets a fresh channel on reconnect.
/// </summary>
internal sealed class PaneStreamTerminalWire : ITerminalWire, IAsyncDisposable
{
    private readonly IPaneStreamConnection _connection;
    private readonly string _stream;
    private readonly int _capacity;
    private readonly Action<PaneStreamTerminalWire> _release;
    private readonly SemaphoreSlim _subscribeGate = new(1, 1);
    private readonly Lock _channelGate = new();
    private Channel<TerminalFrame> _channel;
    private bool _incomingOpen;
    private volatile bool _active;
    private volatile bool _subscribed;
    private int _disposed;

    public PaneStreamTerminalWire(IPaneStreamConnection connection, string terminalId, string stream, int capacity, Action<PaneStreamTerminalWire> release)
    {
        _connection = connection;
        TerminalId = terminalId;
        _stream = stream;
        _capacity = capacity;
        _release = release;
        _channel = NewChannel(capacity);
        _incomingOpen = true;
    }

    /// <summary>The terminal id this wire carries.</summary>
    public string TerminalId { get; }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public ChannelReader<TerminalFrame> Incoming
    {
        get
        {
            lock (_channelGate)
            {
                return _channel.Reader;
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The frame carries another terminal id, or cannot be encoded.</exception>
    /// <exception cref="ObjectDisposedException">The wire is disposed.</exception>
    public async ValueTask SendAsync(TerminalFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (frame.TerminalId != TerminalId)
        {
            throw new ArgumentException("The frame belongs to another terminal.", nameof(frame));
        }

        var encoded = TerminalFrameCodec.Encode(frame);
        if (encoded.Bytes is not { } bytes)
        {
            throw new ArgumentException(encoded.Detail, nameof(frame));
        }

        _active = true;
        await EnsureSubscribedAsync(ct).ConfigureAwait(false);
        await _connection.SendAsync(_stream, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Offers one decoded frame. Full channels drop their oldest frame.</summary>
    internal void Deliver(TerminalFrame frame)
    {
        lock (_channelGate)
        {
            _channel.Writer.TryWrite(frame);
        }
    }

    /// <summary>The connection closed: the subscription is gone and <see cref="Incoming"/> completes.</summary>
    internal void Disconnect()
    {
        _subscribed = false;
        lock (_channelGate)
        {
            if (_incomingOpen)
            {
                _incomingOpen = false;
                _channel.Writer.TryComplete();
            }
        }
    }

    /// <summary>Called after a reconnect: a fresh incoming channel if the old one closed, then a re-subscribe.</summary>
    internal async ValueTask ReopenAsync(CancellationToken ct)
    {
        if (!_active || IsDisposed)
        {
            return;
        }

        lock (_channelGate)
        {
            if (!_incomingOpen)
            {
                _channel = NewChannel(_capacity);
                _incomingOpen = true;
            }
        }

        await _subscribeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsDisposed)
            {
                return;
            }

            await _connection.SubscribeAsync(_stream, ct).ConfigureAwait(false);
            _subscribed = true;
        }
        finally
        {
            _subscribeGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Unsubscribes the stream (best effort) and completes <see cref="Incoming"/>. Waits for an in-flight subscribe.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _active = false;
        _release(this);
        await _subscribeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_subscribed)
            {
                try
                {
                    await _connection.UnsubscribeAsync(_stream, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best effort: the server drops the subscription when the connection goes away.
                }
            }
        }
        finally
        {
            _subscribeGate.Release();
        }

        Disconnect();
    }

    private async ValueTask EnsureSubscribedAsync(CancellationToken ct)
    {
        if (_subscribed)
        {
            return;
        }

        await _subscribeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (!_subscribed)
            {
                await _connection.SubscribeAsync(_stream, ct).ConfigureAwait(false);
                _subscribed = true;
            }
        }
        finally
        {
            _subscribeGate.Release();
        }
    }

    private static Channel<TerminalFrame> NewChannel(int capacity) =>
        Channel.CreateBounded<TerminalFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
}
