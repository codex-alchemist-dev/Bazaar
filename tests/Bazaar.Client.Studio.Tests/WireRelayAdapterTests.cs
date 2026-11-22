using System.Text.Json;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Studio.Tests;

public sealed class WireRelayAdapterTests
{
    private static readonly JsonWireCodec _codec = new();

    private sealed class NoTransport : IWireTransportFactory
    {
        public ValueTask<IWireConnection> ConnectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<IWireConnection>(new InvalidOperationException("offline"));
    }

    private static WireClient Client() =>
        new(new NoTransport(), _codec, new InMemoryCursorStore(), new WireClientOptions { DeviceId = "d", DeviceClass = "windows", ClientBuild = "t" }, TimeProvider.System);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Envelope EventOf(EngineRelayRequestEvent evt) =>
        new(1, EngineRelayWire.EventType, EnvelopeKind.Event, null, null, EngineRelayWire.Stream, null, DateTimeOffset.UtcNow,
            _codec.EncodePayload(EngineRelayFrameMapping.ToFrame(evt)));

    private static async Task<EngineRelayRequestEvent?> FirstAsync(WireRelayEventSource source, TimeSpan wait)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(wait);
        try
        {
            await foreach (var evt in source.ReadAsync(cts.Token))
            {
                return evt;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        return null;
    }

    [Fact]
    public async Task A_relay_event_on_the_stream_is_decoded_and_yielded()
    {
        var source = new WireRelayEventSource(Client(), _codec);
        var sent = new EngineRelayRequestEvent(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), new EngineRelayCall("g1", "studio-1", "multi_edit", Json("{\"x\":1}")));
        source.Offer(EventOf(sent));

        var got = await FirstAsync(source, TimeSpan.FromSeconds(5));

        Assert.NotNull(got);
        Assert.Equal(sent.RelayId, got.RelayId);
        Assert.Equal("multi_edit", got.Call.Tool);
        Assert.Equal(1, got.Call.Args.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task Other_message_types_and_unreadable_frames_are_dropped()
    {
        var source = new WireRelayEventSource(Client(), _codec);
        source.Offer(new Envelope(1, "ping", EnvelopeKind.Event, null, null, EngineRelayWire.Stream, null, null, default));
        source.Offer(new Envelope(1, EngineRelayWire.EventType, EnvelopeKind.Event, null, null, EngineRelayWire.Stream, null, null, "{nope"u8.ToArray()));
        source.Offer(new Envelope(1, EngineRelayWire.EventType, EnvelopeKind.Response, null, null, EngineRelayWire.Stream, null, null, default));

        Assert.Null(await FirstAsync(source, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_full_queue_drops_the_oldest_event()
    {
        var source = new WireRelayEventSource(Client(), _codec);
        var ids = Enumerable.Range(0, WireRelayEventSource.Capacity + 1).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
        {
            source.Offer(EventOf(new EngineRelayRequestEvent(id, DateTimeOffset.UtcNow.AddMinutes(1), new EngineRelayCall("g", "s", "t", Json("{}")))));
        }

        var first = await FirstAsync(source, TimeSpan.FromSeconds(5));

        Assert.Equal(ids[1], first!.RelayId);
    }

    [Fact]
    public async Task Sending_an_answer_while_offline_waits_and_honours_cancellation()
    {
        var sender = new WireRelayResultSender(Client());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sender.SendAsync(new EngineRelayResultRequest(Guid.NewGuid(), new EngineRelayCallResult("g", true, Json("{}"))), cts.Token));
    }
}
