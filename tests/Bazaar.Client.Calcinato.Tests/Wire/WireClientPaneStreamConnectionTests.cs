using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Calcinato.Wire;
using Bazaar.Contracts.Terminal;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Calcinato.Tests.Wire;

/// <summary>The pane adapter over the real <see cref="WireClient"/> and a scripted socket (doc 55, section 6.1).</summary>
public sealed class WireClientPaneStreamConnectionTests : IAsyncLifetime
{
    private const string Stream = "pane:t1";

    private readonly ScriptedTransport _transport = new();
    private readonly WireClient _client;
    private readonly WireClientPaneStreamConnection _adapter;
    private readonly CancellationTokenSource _run = new();
    private Task _runTask = Task.CompletedTask;

    public WireClientPaneStreamConnectionTests()
    {
        _client = new WireClient(_transport, _transport.Codec, new InMemoryCursorStore(),
            new WireClientOptions
            {
                DeviceId = "test",
                DeviceClass = "linux",
                ClientBuild = "test",
                BackoffStart = TimeSpan.FromMilliseconds(5),
                BackoffMax = TimeSpan.FromMilliseconds(5),
                Jitter = () => 1.0,
            },
            TimeProvider.System);
        _adapter = new WireClientPaneStreamConnection(_client, _transport.Codec);
    }

    public ValueTask InitializeAsync()
    {
        _runTask = _client.RunAsync(_run.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _adapter.Dispose();
        await _run.CancelAsync();
        await _runTask;
        _run.Dispose();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<PaneStreamBody> Next(ChannelReader<PaneStreamBody> reader)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        return await reader.ReadAsync(cts.Token);
    }

    private static string Text(PaneStreamBody b) => Encoding.UTF8.GetString(b.Body.Span);

    private static string FrameText(TerminalFrame frame) => Encoding.UTF8.GetString(TerminalFrameCodec.Encode(frame).Bytes!);

    [Fact]
    public async Task Pane_frame_events_become_bodies_with_the_frame_text()
    {
        await _adapter.SubscribeAsync(Stream, TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1);
        var conn = _transport.Latest;
        string frame = FrameText(new TerminalOutput("t1", 1, [104, 105]));

        conn.PushPaneFrame(Stream, frame);

        var body = await Next(_adapter.Bodies);
        Assert.Equal(Stream, body.Stream);
        Assert.Equal(frame, Text(body));
    }

    [Fact]
    public async Task Subscribe_sends_a_subscribe_for_the_stream_and_unsubscribe_sends_unsubscribe()
    {
        await _adapter.SubscribeAsync(Stream, TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Count > 0);
        var conn = _transport.Latest;
        Assert.Equal(Stream, _transport.Codec.DecodePayload<Subscribe>(conn.SentOfType(WireTypes.Subscribe).Single()).Streams.Single().S);

        await _adapter.UnsubscribeAsync(Stream, TestContext.Current.CancellationToken);

        await Until(() => conn.SentOfType(WireTypes.Unsubscribe).Count > 0);
        Assert.Equal([Stream], _transport.Codec.DecodePayload<Unsubscribe>(conn.SentOfType(WireTypes.Unsubscribe).Single()).Streams);
    }

    [Fact]
    public async Task Send_makes_a_pane_frame_request_on_the_stream_and_completes_on_ok()
    {
        await _adapter.SubscribeAsync(Stream, TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1);
        var conn = _transport.Latest;
        string frame = FrameText(new TerminalInput("t1", [108, 115]));

        var send = _adapter.SendAsync(Stream, Encoding.UTF8.GetBytes(frame), TestContext.Current.CancellationToken).AsTask();
        await Until(() => conn.SentOfType("pane.frame").Count > 0);
        var request = conn.SentOfType("pane.frame").Single();

        Assert.Equal(EnvelopeKind.Request, request.K);
        Assert.Equal(Stream, request.S);
        using (var doc = JsonDocument.Parse(request.P))
        {
            Assert.Equal(frame, doc.RootElement.GetProperty("frame").GetString());
        }

        Assert.False(send.IsCompleted);
        conn.Push(new Envelope(1, "pane.frame", EnvelopeKind.Response, null, request.Id, null, null, DateTimeOffset.UnixEpoch, default));
        await send;
    }

    [Fact]
    public async Task An_error_answer_throws_a_wire_request_exception()
    {
        await _adapter.SubscribeAsync(Stream, TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1);
        var conn = _transport.Latest;

        var send = _adapter.SendAsync(Stream, "{}"u8.ToArray(), TestContext.Current.CancellationToken).AsTask();
        await Until(() => conn.SentOfType("pane.frame").Count > 0);
        var request = conn.SentOfType("pane.frame").Single();
        conn.Push(new Envelope(1, "pane.frame", EnvelopeKind.Error, null, request.Id, null, null, DateTimeOffset.UnixEpoch,
            _transport.Codec.EncodePayload(new WireError("forbidden", "no writer", null))));

        var ex = await Assert.ThrowsAsync<WireRequestException>(() => send);
        Assert.Equal("forbidden", ex.Error.Code);
    }

    [Fact]
    public async Task Unknown_streams_and_other_types_and_bad_payloads_are_ignored()
    {
        await _adapter.SubscribeAsync(Stream, TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1);
        var conn = _transport.Latest;

        conn.PushPaneFrame("other:t1", "ignored-stream");
        conn.PushRaw("pane.other", Stream, "{\"frame\":\"wrong-type\"}"u8.ToArray());
        conn.PushRaw("pane.frame", Stream, "{\"frame\":5}"u8.ToArray());
        conn.PushRaw("pane.frame", Stream, "[1]"u8.ToArray());
        conn.PushRaw("pane.frame", "pane:bad id", "{\"frame\":\"bad-id\"}"u8.ToArray());
        conn.PushPaneFrame(Stream, "last");

        var body = await Next(_adapter.Bodies);

        Assert.Equal("last", Text(body));
        Assert.Equal(1, _client.ConnectionCount);
    }

    [Fact]
    public async Task A_second_sink_for_the_pane_prefix_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => new WireClientPaneStreamConnection(_client, _transport.Codec));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Reconnect_resubscribes_resends_the_unanswered_request_with_the_same_id_and_raises_Reconnected()
    {
        int reconnected = 0;
        _adapter.Reconnected += () => Interlocked.Increment(ref reconnected);
        await _adapter.SubscribeAsync(Stream, TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1);
        var first = _transport.Latest;
        var send = _adapter.SendAsync(Stream, "{\"x\":1}"u8.ToArray(), TestContext.Current.CancellationToken).AsTask();
        await Until(() => first.SentOfType("pane.frame").Count > 0);
        string id = first.SentOfType("pane.frame").Single().Id!;

        first.Close();
        await Until(() => _transport.Count == 2 && _transport.Latest.SentOfType("pane.frame").Count > 0);
        var second = _transport.Latest;

        Assert.Equal(id, second.SentOfType("pane.frame").Single().Id);
        Assert.Equal(Stream, _transport.Codec.DecodePayload<Subscribe>(second.SentOfType(WireTypes.Subscribe).Single()).Streams.Single().S);
        await Until(() => Volatile.Read(ref reconnected) == 1);

        second.Push(new Envelope(1, "pane.frame", EnvelopeKind.Response, null, id, null, null, DateTimeOffset.UnixEpoch, default));
        await send;

        // The same body reader keeps delivering after the reconnect.
        second.PushPaneFrame(Stream, "after");
        Assert.Equal("after", Text(await Next(_adapter.Bodies)));
    }

    [Fact]
    public async Task An_unsubscribed_stream_is_not_resubscribed_after_a_reconnect()
    {
        await _adapter.SubscribeAsync(Stream, TestContext.Current.CancellationToken);
        await _adapter.SubscribeAsync("pane:t2", TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1);
        await _adapter.UnsubscribeAsync(Stream, TestContext.Current.CancellationToken);

        _transport.Latest.Close();
        await Until(() => _transport.Count == 2 && _transport.Latest.SentOfType(WireTypes.Subscribe).Count > 0);

        var streams = _transport.Codec.DecodePayload<Subscribe>(_transport.Latest.SentOfType(WireTypes.Subscribe).First()).Streams;
        Assert.Equal(["pane:t2"], streams.Select(s => s.S));
    }

    [Fact]
    public async Task Disposing_the_adapter_completes_Bodies_and_frees_the_prefix()
    {
        _adapter.Dispose();

        await _adapter.Bodies.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var again = new WireClientPaneStreamConnection(_client, _transport.Codec);
    }

    [Fact]
    public async Task A_non_pane_event_still_reaches_no_sink_and_the_client_keeps_running()
    {
        await _adapter.SubscribeAsync(Stream, TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1);

        _transport.Latest.PushRaw("future.event", "future:1", "{}"u8.ToArray());
        _transport.Latest.PushPaneFrame(Stream, "still-alive");

        Assert.Equal("still-alive", Text(await Next(_adapter.Bodies)));
    }

    [Fact]
    public async Task Frames_for_the_hub_flow_through_to_a_terminal_wire()
    {
        await using var hub = new PaneStreamTerminalWireHub(_adapter);
        var wire = hub.ForTerminal("t1");

        var send = wire.SendAsync(new TerminalResize("t1", 80, 24), TestContext.Current.CancellationToken).AsTask();
        await Until(() => _transport.Count == 1);
        var conn = _transport.Latest;
        await Until(() => conn.SentOfType("pane.frame").Count > 0);
        var request = conn.SentOfType("pane.frame").Single();
        conn.Push(new Envelope(1, "pane.frame", EnvelopeKind.Response, null, request.Id, null, null, DateTimeOffset.UnixEpoch, default));
        await send;
        conn.PushPaneFrame(Stream, FrameText(new TerminalOutput("t1", 7, [65])));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var frame = await wire.Incoming.ReadAsync(cts.Token);
        Assert.Equal(new TerminalOutput("t1", 7, [65]).Seq, ((TerminalOutput)frame).Seq);
    }

    [Fact]
    public void A_non_json_codec_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => new WireClientPaneStreamConnection(_client, new NamedCodec("cbor", _transport.Codec)));
    }

    [Fact]
    public async Task Non_pane_stream_names_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await _adapter.SubscribeAsync("gc:1", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () => await _adapter.SendAsync("pane:", "{}"u8.ToArray(), TestContext.Current.CancellationToken));
    }

    private sealed class NamedCodec(string name, IWireCodec inner) : IWireCodec
    {
        public string Name => name;
        public bool IsBinary => inner.IsBinary;
        public void Encode(in Envelope envelope, IBufferWriter<byte> writer) => inner.Encode(envelope, writer);
        public Envelope Decode(ReadOnlySpan<byte> frame) => inner.Decode(frame);
        public ReadOnlyMemory<byte> EncodePayload<T>(T payload) => inner.EncodePayload(payload);
        public T DecodePayload<T>(in Envelope envelope) => inner.DecodePayload<T>(envelope);
    }

    private sealed class ScriptedTransport : IWireTransportFactory
    {
        private readonly List<ScriptedConnection> _connections = [];

        public JsonWireCodec Codec { get; } = new();

        public int Count
        {
            get { lock (_connections) return _connections.Count; }
        }

        public ScriptedConnection Latest
        {
            get { lock (_connections) return _connections[^1]; }
        }

        public ValueTask<IWireConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            var conn = new ScriptedConnection(this);
            lock (_connections) _connections.Add(conn);
            return ValueTask.FromResult<IWireConnection>(conn);
        }
    }

    private sealed class ScriptedConnection(ScriptedTransport owner) : IWireConnection
    {
        private readonly Channel<WireReceive> _inbound = Channel.CreateUnbounded<WireReceive>();
        private readonly List<Envelope> _sent = [];

        public List<Envelope> SentOfType(string type)
        {
            lock (_sent) return _sent.Where(e => e.T == type).ToList();
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            var env = owner.Codec.Decode(frame.Span);
            lock (_sent) _sent.Add(env);
            if (env.T == WireTypes.Hello)
            {
                Push(new Envelope(1, WireTypes.Welcome, EnvelopeKind.Response, null, env.Id, null, null, DateTimeOffset.UnixEpoch,
                    owner.Codec.EncodePayload(new Welcome(1, 0, "json", Guid.Empty, DateTimeOffset.UnixEpoch, 20, 60,
                        new WireLimits(262144, 32, 200), [], 1, null, null))));
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

        public void PushRaw(string type, string stream, byte[] payload) =>
            Push(new Envelope(1, type, EnvelopeKind.Event, null, null, stream, null, DateTimeOffset.UnixEpoch, payload));

        public void PushPaneFrame(string stream, string frame)
        {
            var w = new ArrayBufferWriter<byte>();
            using (var json = new Utf8JsonWriter(w))
            {
                json.WriteStartObject();
                json.WriteString("frame"u8, frame);
                json.WriteEndObject();
            }

            PushRaw("pane.frame", stream, w.WrittenSpan.ToArray());
        }

        public void Close(int code = 0) => _inbound.Writer.TryWrite(WireReceive.Closed(code));
    }
}
