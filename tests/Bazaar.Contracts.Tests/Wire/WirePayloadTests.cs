using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Tests.Wire;

public class WirePayloadTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _author = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");
    private static readonly Guid _msg = Guid.Parse("0196a6f0-0000-7000-8000-000000000004");
    private static readonly DateTimeOffset _at = new(2026, 10, 25, 5, 36, 0, TimeSpan.Zero);

    private static WireGcMessage Message(long n = 12, long ev = 40, bool edited = false) =>
        new(_gc, n, ev, _msg, _author, "hello", _at, edited, [_author], ReplyToN: null);

    public static TheoryData<string, object> Samples() => new()
    {
        { "hello", new Hello(1, 1, 0, ["json"], "dev-1", "windows", "0.1.0", null, [new StreamCursor("gc:" + _gc, 11, 39)]) },
        { "welcome", new Welcome(1, 0, "json", _author, _at, 20, 60, new WireLimits(262144, 32, 200), ["resync"], 7, null, null) },
        { "subscribe", new Subscribe([new StreamCursor(WireStreams.Gc(_gc), 11, 39)]) },
        { "subscribe-result", new SubscribeResult([new StreamResult(WireStreams.Gc(_gc), SubscribeStatuses.Ok)]) },
        { "gc-message", Message() },
        { "gc-message-edit", Message(n: 12, ev: 41, edited: true) },
        { "gc-send", new GcSend(_gc, "hello", null, null) },
        { "gc-page", new GcPage([Message()], HasMore: true) },
        { "resync", new Resync(WireStreams.Gc(_gc), 5000, 5100, 3, [Message()]) },
        { "lagged", new Lagged(WireStreams.Gc(_gc), 12) },
        { "error", new WireError(WireErrorCodes.RateLimited, null, 1500) },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Payload_matches_its_golden_file(string name, object payload)
    {
        GoldenFile.Check(name + ".json", Serialize(payload));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Payload_round_trips_with_an_unknown_field_at_every_level(string name, object payload)
    {
        _ = name;
        string json = Serialize(payload);
        var node = JsonNode.Parse(json)!;
        AddUnknownField(node);

        object? back = JsonSerializer.Deserialize(node.ToJsonString(), payload.GetType(), WireJsonContext.Default);

        Assert.Equivalent(payload, back, strict: true);
    }

    [Fact]
    public void Missing_optional_fields_read_as_null()
    {
        var back = JsonSerializer.Deserialize("""{"gcId":"0196a6f0-0000-7000-8000-000000000001","text":"hi"}""",
            WireJsonContext.Default.GcSend);

        Assert.NotNull(back);
        Assert.Null(back.Mentions);
        Assert.Null(back.ReplyToN);
    }

    [Fact]
    public void Null_fields_are_left_out_of_the_frame()
    {
        string json = Serialize(new WireError(WireErrorCodes.Unsupported, null, null));

        Assert.Equal("""{"code":"unsupported"}""", json);
    }

    [Fact]
    public void Edit_keeps_the_message_n_and_raises_ev()
    {
        var original = Message(n: 12, ev: 40);
        var edit = original with { Ev = 41, Edited = true };

        Assert.Equal(original.N, edit.N);
        Assert.True(edit.Ev > original.Ev);
    }

    [Fact]
    public void Wire_names_are_lower_case_and_round_trip()
    {
        foreach (EnvelopeKind kind in Enum.GetValues<EnvelopeKind>())
        {
            string wire = EnvelopeKinds.ToWire(kind);
            Assert.Equal(wire, wire.ToLowerInvariant());
            Assert.True(EnvelopeKinds.TryParse(wire, out var parsed));
            Assert.Equal(kind, parsed);
        }

        Assert.False(EnvelopeKinds.TryParse("nope", out _));
        Assert.False(EnvelopeKinds.TryParse(null, out _));
    }

    [Fact]
    public void Gc_stream_names_round_trip()
    {
        string name = WireStreams.Gc(_gc);

        Assert.Equal("gc:0196a6f0-0000-7000-8000-000000000001", name);
        Assert.True(WireStreams.TryParseGc(name, out var id));
        Assert.Equal(_gc, id);
        Assert.False(WireStreams.TryParseGc("presence", out _));
        Assert.False(WireStreams.TryParseGc("gc:not-a-guid", out _));
        Assert.False(WireStreams.TryParseGc(null, out _));
    }

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, payload.GetType(), WireJsonContext.Default);

    private static void AddUnknownField(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var child in obj.Select(p => p.Value).Where(v => v is not null).ToList()) AddUnknownField(child!);
                obj["zzFutureField"] = new JsonObject { ["x"] = 1 };
                break;
            case JsonArray arr:
                foreach (var child in arr) if (child is not null) AddUnknownField(child);
                break;
        }
    }
}
