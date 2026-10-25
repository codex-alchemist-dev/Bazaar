using System.Buffers;
using System.Threading.Channels;
using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Core.Tests.Wire;

/// <summary>Scripted server side of the socket. Replies <c>welcome</c> to each <c>hello</c>.</summary>
internal sealed class FakeWireTransport : IWireTransportFactory
{
    public JsonWireCodec Codec { get; } = new();

    public List<FakeConnection> Connections { get; } = [];

    /// <summary>When set, the next connect attempts throw instead of opening.</summary>
    public int FailNextConnects { get; set; }

    public bool WelcomeUpgradeRequired { get; set; }

    public ValueTask<IWireConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (FailNextConnects > 0)
        {
            FailNextConnects--;
            throw new InvalidOperationException("server unreachable");
        }

        var conn = new FakeConnection(this);
        lock (Connections) Connections.Add(conn);
        return ValueTask.FromResult<IWireConnection>(conn);
    }

    public FakeConnection Latest
    {
        get { lock (Connections) return Connections[^1]; }
    }

    public int Count
    {
        get { lock (Connections) return Connections.Count; }
    }
}

internal sealed class FakeConnection(FakeWireTransport owner) : IWireConnection
{
    private readonly Channel<WireReceive> _inbound = Channel.CreateUnbounded<WireReceive>();
    private readonly List<Envelope> _sent = [];

    public IReadOnlyList<Envelope> Sent
    {
        get { lock (_sent) return [.. _sent]; }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        var env = owner.Codec.Decode(frame.Span);
        lock (_sent) _sent.Add(env);
        if (env.T == WireTypes.Hello)
        {
            Push(new Envelope(1, WireTypes.Welcome, EnvelopeKind.Response, null, env.Id, null, null, DateTimeOffset.UnixEpoch,
                owner.Codec.EncodePayload(new Welcome(1, 0, "json", Guid.Empty, DateTimeOffset.UnixEpoch, 20, 60,
                    new WireLimits(262144, 32, 200), [], 1, owner.WelcomeUpgradeRequired ? true : null, null))));
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken) =>
        await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Push(Envelope env)
    {
        var w = new ArrayBufferWriter<byte>();
        owner.Codec.Encode(env, w);
        _inbound.Writer.TryWrite(WireReceive.Data(w.WrittenSpan.ToArray()));
    }

    public void PushEvent<T>(string type, T payload, string? stream = null, long? n = null) =>
        Push(new Envelope(1, type, EnvelopeKind.Event, null, null, stream, n, DateTimeOffset.UnixEpoch, owner.Codec.EncodePayload(payload)));

    public void Close(int code = 0) => _inbound.Writer.TryWrite(WireReceive.Closed(code));

    public IEnumerable<Envelope> SentOfType(string type) => Sent.Where(e => e.T == type);
}
