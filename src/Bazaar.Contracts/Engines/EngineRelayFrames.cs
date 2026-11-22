using System.Text.Json;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Engines;

/// <summary>
/// The relay call as it crosses the wire (doc 02, doc 90). The codecs reject open-ended JSON values, so the tool arguments travel as
/// a JSON text. <see cref="EngineRelayFrameMapping"/> converts to and from <see cref="EngineRelayCall"/>.
/// </summary>
/// <param name="GrantId">The grant reference the server issued.</param>
/// <param name="StudioId">The target Studio.</param>
/// <param name="Tool">The Studio MCP tool name.</param>
/// <param name="ArgsJson">The tool arguments as the text of one JSON object.</param>
public sealed record EngineRelayCallFrame(string GrantId, string StudioId, string Tool, string ArgsJson);

/// <summary>The relay answer as it crosses the wire; the payload travels as a JSON text.</summary>
/// <param name="GrantId">Echo of the request's grant reference.</param>
/// <param name="Ok">True when Studio ran the tool and reported no tool error.</param>
/// <param name="PayloadJson">The MCP result or the error object, as JSON text.</param>
public sealed record EngineRelayCallResultFrame(string GrantId, bool Ok, string PayloadJson);

/// <summary>Wire payload of the server event <see cref="EngineRelayWire.EventType"/>.</summary>
/// <param name="RelayId">Correlation id.</param>
/// <param name="Deadline">Absolute time after which the server no longer waits.</param>
/// <param name="Call">The call to run.</param>
[WirePayload(EngineRelayWire.EventType)]
public sealed record EngineRelayRequestFrame(Guid RelayId, DateTimeOffset Deadline, EngineRelayCallFrame Call);

/// <summary>Wire payload of the client request <see cref="EngineRelayWire.RequestType"/>.</summary>
/// <param name="RelayId">The id from the event.</param>
/// <param name="Result">What the relay returned.</param>
[WirePayload(EngineRelayWire.RequestType)]
public sealed record EngineRelayResultFrame(Guid RelayId, EngineRelayCallResultFrame Result);

/// <summary>Conversion between the relay's domain records and its wire frames. A frame that is not valid JSON is a <see cref="FormatException"/>.</summary>
public static class EngineRelayFrameMapping
{
    /// <summary>Domain event to frame.</summary>
    public static EngineRelayRequestFrame ToFrame(EngineRelayRequestEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(source.RelayId, source.Deadline, new(source.Call.GrantId, source.Call.StudioId, source.Call.Tool, source.Call.Args.GetRawText()));
    }

    /// <summary>Frame to domain event.</summary>
    public static EngineRelayRequestEvent ToEvent(EngineRelayRequestFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new(frame.RelayId, frame.Deadline, new(frame.Call.GrantId, frame.Call.StudioId, frame.Call.Tool, Parse(frame.Call.ArgsJson)));
    }

    /// <summary>Domain result request to frame.</summary>
    public static EngineRelayResultFrame ToFrame(EngineRelayResultRequest source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(source.RelayId, new(source.Result.GrantId, source.Result.Ok, source.Result.Payload.GetRawText()));
    }

    /// <summary>Frame to domain result request.</summary>
    public static EngineRelayResultRequest ToRequest(EngineRelayResultFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new(frame.RelayId, new(frame.Result.GrantId, frame.Result.Ok, Parse(frame.Result.PayloadJson)));
    }

    private static JsonElement Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FormatException("The relay frame carries JSON text that does not parse.", ex);
        }
    }
}
