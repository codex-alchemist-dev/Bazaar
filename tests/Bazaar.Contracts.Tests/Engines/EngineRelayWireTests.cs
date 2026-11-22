using System.Text.Json;
using Bazaar.Contracts.Engines;

namespace Bazaar.Contracts.Tests.Engines;

public sealed class EngineRelayWireTests
{
    private static readonly Guid _relay = Guid.Parse("0196a6f0-0000-7000-8000-0000000000aa");

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Wire_names_are_stable()
    {
        Assert.Equal("engine.relay", EngineRelayWire.Stream);
        Assert.Equal("engine.relay.request", EngineRelayWire.EventType);
        Assert.Equal("engine.relay.result", EngineRelayWire.RequestType);
        Assert.Equal("engine.relay.timeout_seconds", EngineRelayWire.TimeoutSetting);
        Assert.Equal(60, EngineRelayWire.DefaultTimeoutSeconds);
    }

    [Fact]
    public void Request_event_serialises_camel_case_and_round_trips()
    {
        var evt = new EngineRelayRequestEvent(
            _relay,
            new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero),
            new EngineRelayCall("g1", "studio-A", "multi_edit", Json("{\"path\":\"a\"}")));

        var json = JsonSerializer.Serialize(evt, EngineRelayJsonContext.Default.EngineRelayRequestEvent);

        Assert.Equal(
            "{\"relayId\":\"0196a6f0-0000-7000-8000-0000000000aa\",\"deadline\":\"2026-11-18T12:00:00+00:00\","
            + "\"call\":{\"grantId\":\"g1\",\"studioId\":\"studio-A\",\"tool\":\"multi_edit\",\"args\":{\"path\":\"a\"}}}",
            json);
        var back = JsonSerializer.Deserialize(json, EngineRelayJsonContext.Default.EngineRelayRequestEvent)!;
        Assert.Equal(_relay, back.RelayId);
        Assert.Equal(evt.Deadline, back.Deadline);
        Assert.Equal("a", back.Call.Args.GetProperty("path").GetString());
    }

    [Fact]
    public void Result_request_serialises_camel_case_and_round_trips()
    {
        var request = new EngineRelayResultRequest(_relay, new EngineRelayCallResult("g1", false, Json("{\"error\":{\"code\":\"Timeout\"}}")));

        var json = JsonSerializer.Serialize(request, EngineRelayJsonContext.Default.EngineRelayResultRequest);

        Assert.Equal(
            "{\"relayId\":\"0196a6f0-0000-7000-8000-0000000000aa\",\"result\":{\"grantId\":\"g1\",\"ok\":false,\"payload\":{\"error\":{\"code\":\"Timeout\"}}}}",
            json);
        var back = JsonSerializer.Deserialize(json, EngineRelayJsonContext.Default.EngineRelayResultRequest)!;
        Assert.False(back.Result.Ok);
        Assert.Equal("Timeout", back.Result.Payload.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Unknown_members_are_skipped()
    {
        var json = "{\"relayId\":\"0196a6f0-0000-7000-8000-0000000000aa\",\"extra\":1,\"result\":{\"grantId\":\"g\",\"ok\":true,\"payload\":{}}}";
        var back = JsonSerializer.Deserialize(json, EngineRelayJsonContext.Default.EngineRelayResultRequest)!;
        Assert.Equal(_relay, back.RelayId);
    }
}
