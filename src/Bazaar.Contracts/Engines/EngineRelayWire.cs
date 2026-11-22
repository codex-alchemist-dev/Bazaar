using System.Text.Json;

namespace Bazaar.Contracts.Engines;

/// <summary>
/// Wire names of the Studio relay-request channel [PROPOSED, coordinator brief 2026-11-18] (docs/design/02-wire-protocol-and-sync.md,
/// stream registry; owner doc 20). The wire has no server-to-client request, so the server publishes a relay request as an
/// <c>evt</c> on a state stream and the client answers with an ordinary client <c>req</c>.
/// </summary>
public static class EngineRelayWire
{
    /// <summary>The state stream the server publishes relay requests on. Only the owner's Windows desktop device subscribes.</summary>
    public const string Stream = "engine.relay";

    /// <summary>Server <c>evt</c> type carrying an <see cref="EngineRelayRequestEvent"/>.</summary>
    public const string EventType = Bazaar.Contracts.Wire.WireTypes.EngineRelayRequest;

    /// <summary>Client <c>req</c> type carrying an <see cref="EngineRelayResultRequest"/>. Idempotent by envelope id like any request.</summary>
    public const string RequestType = Bazaar.Contracts.Wire.WireTypes.EngineRelayResult;

    /// <summary>Setting key for the per-call deadline in seconds (doc 20).</summary>
    public const string TimeoutSetting = "engine.relay.timeout_seconds";

    /// <summary>Default per-call deadline in seconds.</summary>
    public const int DefaultTimeoutSeconds = 60;
}

/// <summary>
/// The Studio call a relay request carries. Same shape as <c>StudioRelayRequest</c> in <c>Bazaar.Client.Studio.Abstractions</c>
/// (doc 28, Contracts); defined here because Contracts cannot reference that project.
/// </summary>
/// <param name="GrantId">The grant reference the server issued.</param>
/// <param name="StudioId">The target Studio. Required; there is no default (doc 20).</param>
/// <param name="Tool">The Studio MCP tool name.</param>
/// <param name="Args">The tool arguments, a JSON object.</param>
public sealed record EngineRelayCall(string GrantId, string StudioId, string Tool, JsonElement Args);

/// <summary>The outcome of a relay call. Same shape as <c>StudioRelayResult</c>; on failure <paramref name="Payload"/> holds an error object.</summary>
/// <param name="GrantId">Echo of the request's grant reference.</param>
/// <param name="Ok">True when Studio ran the tool and reported no tool error.</param>
/// <param name="Payload">The MCP result (untrusted data) or the error payload.</param>
public sealed record EngineRelayCallResult(string GrantId, bool Ok, JsonElement Payload);

/// <summary>Server event on <see cref="EngineRelayWire.Stream"/>: run this call and answer with an <see cref="EngineRelayResultRequest"/>.</summary>
/// <param name="RelayId">Correlation id, unguessable, valid for one answer from the device the event went to.</param>
/// <param name="Deadline">Absolute time after which the server no longer waits; the client drops later events.</param>
/// <param name="Call">The call to run.</param>
public sealed record EngineRelayRequestEvent(Guid RelayId, DateTimeOffset Deadline, EngineRelayCall Call);

/// <summary>Client request of type <see cref="EngineRelayWire.RequestType"/>: the answer to one relay event.</summary>
/// <param name="RelayId">The id from the event.</param>
/// <param name="Result">What the relay returned.</param>
public sealed record EngineRelayResultRequest(Guid RelayId, EngineRelayCallResult Result);
