using System.Collections.Concurrent;
using System.Threading.Channels;
using Bazaar.Client.Transport.AndroidHttp3;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Tests;

/// <summary>In-memory Cronet: the "server" side is a channel of bytes the test feeds.</summary>
internal sealed class FakeCronetStream : ICronetBidirectionalStream
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private byte[] _pending = [];
    private int _pendingAt;

    public CronetResponseHead Head { get; set; } = new(200, "h3", new Dictionary<string, string> { ["bazaar-codec"] = "json" });

    public ConcurrentQueue<byte[]> Written { get; } = new();

    public Dictionary<string, string> TrailersValue { get; } = new();

    public bool WritesCompleted { get; private set; }

    public bool Disposed { get; private set; }

    public bool FailReads { get; set; }

    public bool CleanEnd { get; set; } = true;

    public event Action<CronetNetworkEvent>? NetworkEvent;

    public IReadOnlyDictionary<string, string> Trailers => TrailersValue;

    public bool EndedCleanly => CleanEnd;

    public void Feed(byte[] bytes) => _incoming.Writer.TryWrite(bytes);

    public void EndBody() => _incoming.Writer.TryComplete();

    public void Raise(CronetNetworkEvent e) => NetworkEvent?.Invoke(e);

    public ValueTask<CronetResponseHead> ReadResponseHeadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Head);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        Written.Enqueue(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (FailReads) throw new IOException("network reset");
        if (_pendingAt >= _pending.Length)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
            _pending = await _incoming.Reader.ReadAsync(cancellationToken);
            _pendingAt = 0;
        }

        var n = Math.Min(buffer.Length, _pending.Length - _pendingAt);
        _pending.AsMemory(_pendingAt, n).CopyTo(buffer);
        _pendingAt += n;
        return n;
    }

    public ValueTask CompleteWritesAsync(CancellationToken cancellationToken)
    {
        WritesCompleted = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeCronetEngine : ICronetEngine
{
    public bool IsAvailable { get; set; } = true;

    public Exception? OpenFailure { get; set; }

    public FakeCronetStream Stream { get; } = new();

    public List<CronetStreamRequest> Requests { get; } = [];

    public ValueTask<ICronetBidirectionalStream> OpenStreamAsync(CronetStreamRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (OpenFailure is not null) throw OpenFailure;
        return ValueTask.FromResult<ICronetBidirectionalStream>(Stream);
    }
}

internal sealed class RecordingObserver : IWireConnectionObserver
{
    public List<string> Protocols { get; } = [];

    public List<CronetNetworkEvent> Events { get; } = [];

    public List<(ReconnectReason Reason, CronetNetworkEvent? Last)> Reconnects { get; } = [];

    public List<(string From, string Reason)> Fallbacks { get; } = [];

    public void OnNegotiated(string protocol) => Protocols.Add(protocol);

    public void OnNetworkEvent(CronetNetworkEvent networkEvent) => Events.Add(networkEvent);

    public void OnReconnectRequired(ReconnectReason reason, CronetNetworkEvent? lastNetworkEvent) => Reconnects.Add((reason, lastNetworkEvent));

    public void OnFallback(string fromTransport, string reason) => Fallbacks.Add((fromTransport, reason));
}

/// <summary>A connector standing in for grpc over h2 or ws.</summary>
internal sealed class FakeConnector(string transport, Exception? failure = null, bool supported = true) : IWireTransportConnector
{
    public int Calls { get; private set; }

    public string Transport => transport;

    public bool IsSupported => supported;

    public ValueTask<IWireConnection> ConnectAsync(TransportOffer offer, IReadOnlyList<string> codecs, CancellationToken cancellationToken)
    {
        Calls++;
        if (failure is not null) throw failure;
        return ValueTask.FromResult<IWireConnection>(new StubConnection(new WireProfile(transport, codecs[0])));
    }
}

internal sealed class StubConnection(WireProfile profile) : IWireConnection
{
    public WireProfile Profile => profile;

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(WireReceive.Closed());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
