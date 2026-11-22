using System.Text.Json;
using Bazaar.Contracts.Engines;

namespace Bazaar.Contracts.Tests.Engines;

public sealed class EngineRelayFrameTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void The_request_event_round_trips_through_its_frame()
    {
        var evt = new EngineRelayRequestEvent(Guid.NewGuid(), DateTimeOffset.Parse("2026-11-18T12:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
            new EngineRelayCall("g1", "studio-1", "multi_edit", Json("{\"x\":[1,2],\"y\":\"z\"}")));

        var back = EngineRelayFrameMapping.ToEvent(EngineRelayFrameMapping.ToFrame(evt));

        Assert.Equal(evt.RelayId, back.RelayId);
        Assert.Equal(evt.Deadline, back.Deadline);
        Assert.Equal("multi_edit", back.Call.Tool);
        Assert.Equal(2, back.Call.Args.GetProperty("x").GetArrayLength());
    }

    [Fact]
    public void The_result_request_round_trips_through_its_frame()
    {
        var request = new EngineRelayResultRequest(Guid.NewGuid(), new EngineRelayCallResult("g1", false, Json("{\"error\":\"boom\"}")));

        var back = EngineRelayFrameMapping.ToRequest(EngineRelayFrameMapping.ToFrame(request));

        Assert.Equal(request.RelayId, back.RelayId);
        Assert.False(back.Result.Ok);
        Assert.Equal("boom", back.Result.Payload.GetProperty("error").GetString());
    }

    [Fact]
    public void A_frame_with_text_that_is_not_json_is_a_format_error()
    {
        var frame = new EngineRelayResultFrame(Guid.NewGuid(), new EngineRelayCallResultFrame("g1", true, "{nope"));

        Assert.Throws<FormatException>(() => EngineRelayFrameMapping.ToRequest(frame));
    }
}
