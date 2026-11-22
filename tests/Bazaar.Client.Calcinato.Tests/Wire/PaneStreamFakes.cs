using System.Collections.Concurrent;
using System.Threading.Channels;
using Bazaar.Client.Calcinato.Wire;
using Bazaar.Contracts.Terminal;

namespace Bazaar.Client.Calcinato.Tests.Wire;

/// <summary>One body the module sent on a stream.</summary>
internal sealed record SentBody(string Stream, byte[] Body);

/// <summary>
/// In-memory <see cref="IPaneStreamConnection"/>. The test pushes bodies, drops the connection and reconnects it.
/// <see cref="OnSend"/> lets a fake server answer what the module sends.
/// </summary>
internal sealed class FakePaneConnection : IPaneStreamConnection
{
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<string> _subscribes = new();
    private readonly ConcurrentQueue<string> _unsubscribes = new();
    private readonly ConcurrentQueue<SentBody> _sent = new();
    private Channel<PaneStreamBody> _bodies = NewChannel();

    /// <summary>Called synchronously for every send, so a fake server can answer inline.</summary>
    public Action<string, byte[]>? OnSend { get; set; }

    public ChannelReader<PaneStreamBody> Bodies
    {
        get
        {
            lock (_gate)
            {
                return _bodies.Reader;
            }
        }
    }

    public IReadOnlyList<string> Subscribes => _subscribes.ToArray();

    public IReadOnlyList<string> Unsubscribes => _unsubscribes.ToArray();

    public IReadOnlyList<SentBody> Sent => _sent.ToArray();

    public IReadOnlyList<SentBody> SentTo(string stream) => _sent.Where(s => s.Stream == stream).ToArray();

    public void Push(string stream, byte[] body)
    {
        lock (_gate)
        {
            _bodies.Writer.TryWrite(new PaneStreamBody(stream, body));
        }
    }

    public void Push(string stream, TerminalFrame frame) => Push(stream, TerminalFrameCodec.Encode(frame).Bytes!);

    /// <summary>Closes the body reader, as a dropped socket does.</summary>
    public void Drop()
    {
        lock (_gate)
        {
            _bodies.Writer.TryComplete();
        }
    }

    /// <summary>Starts a new body reader, as a reconnected socket does.</summary>
    public void Reconnect()
    {
        lock (_gate)
        {
            _bodies = NewChannel();
        }
    }

    public ValueTask SubscribeAsync(string stream, CancellationToken ct)
    {
        _subscribes.Enqueue(stream);
        return ValueTask.CompletedTask;
    }

    public ValueTask UnsubscribeAsync(string stream, CancellationToken ct)
    {
        _unsubscribes.Enqueue(stream);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendAsync(string stream, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var bytes = body.ToArray();
        _sent.Enqueue(new SentBody(stream, bytes));
        OnSend?.Invoke(stream, bytes);
        return ValueTask.CompletedTask;
    }

    private static Channel<PaneStreamBody> NewChannel() => Channel.CreateUnbounded<PaneStreamBody>();
}

/// <summary>
/// A fake server for one terminal over <see cref="FakePaneConnection"/>. It keeps a ring of every output it produced,
/// answers <see cref="TerminalOpen"/> with <see cref="TerminalOpened"/> and <see cref="TerminalReplay"/> with the ring
/// from that sequence. Live output can be withheld to make a gap.
/// </summary>
internal sealed class FakePaneServer
{
    private readonly FakePaneConnection _connection;
    private readonly string _terminalId;
    private readonly SortedDictionary<long, byte[]> _ring = [];
    private readonly ConcurrentQueue<TerminalFrame> _received = new();

    public FakePaneServer(FakePaneConnection connection, string terminalId)
    {
        _connection = connection;
        _terminalId = terminalId;
        connection.OnSend = Handle;
    }

    public IReadOnlyList<TerminalFrame> Received => _received.ToArray();

    /// <summary>Produces output. With <paramref name="live"/> false it is only kept in the ring.</summary>
    public void Output(long seq, byte value, bool live = true)
    {
        _ring[seq] = [value];
        if (live)
        {
            _connection.Push(WireStreamName, new TerminalOutput(_terminalId, seq, [value]));
        }
    }

    public void Exit(int code) => _connection.Push(WireStreamName, new TerminalExit(_terminalId, code));

    private string WireStreamName => "pane:" + _terminalId;

    private void Handle(string stream, byte[] body)
    {
        var decoded = TerminalFrameCodec.Decode(body);
        if (decoded.Frame is not { } frame)
        {
            return;
        }

        _received.Enqueue(frame);
        switch (frame)
        {
            case TerminalOpen:
                _connection.Push(WireStreamName, new TerminalOpened(_terminalId, WriterHeld: false, ReplayFrom: 1));
                break;
            case TerminalReplay replay:
                foreach (var (seq, bytes) in _ring.Where(kv => kv.Key >= replay.FromSeq))
                {
                    _connection.Push(WireStreamName, new TerminalOutput(_terminalId, seq, bytes));
                }

                break;
        }
    }
}
