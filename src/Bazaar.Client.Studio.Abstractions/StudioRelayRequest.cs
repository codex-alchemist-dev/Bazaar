using System.Text.Json;

namespace Bazaar.Client.Studio.Abstractions;

/// <summary>
/// A Studio relay call from the server (docs/design/28-windows-client.md, Contracts). <paramref name="GrantId"/> points to a durable
/// <c>engine_grants</c> row or approval (docs 15 and 20); the client keeps no grants itself.
/// </summary>
/// <param name="GrantId">The grant reference the server issued.</param>
/// <param name="StudioId">The target Studio. Required: there is no default when several Studios are open (doc 20).</param>
/// <param name="Tool">The Studio MCP tool name.</param>
/// <param name="Args">The tool arguments, a JSON object (or undefined for none).</param>
public sealed record StudioRelayRequest(string GrantId, string StudioId, string Tool, JsonElement Args);

/// <summary>The outcome of a relay call. On failure <paramref name="Payload"/> holds a <see cref="StudioRelayError"/> (see <see cref="StudioRelayError.ToPayload"/>).</summary>
/// <param name="GrantId">Echo of the request's grant reference.</param>
/// <param name="Ok">True when Studio ran the tool and reported no tool error.</param>
/// <param name="Payload">The MCP result (untrusted data, possibly truncated) or the error payload.</param>
public sealed record StudioRelayResult(string GrantId, bool Ok, JsonElement Payload);
