namespace Bazaar.Contracts.Wire;

/// <summary>
/// Payload of <c>pane.frame</c> (docs/design/55, section 6.1): one terminal frame on a <c>pane:&lt;terminalId&gt;</c> stream. Server to
/// client it is an event; client to server it is a request answered <c>ok</c> or an error, and the request id makes a resend
/// idempotent (doc 02).
/// </summary>
/// <param name="Frame">The terminal frame as JSON text. A binary form would be a new optional field, never a change to this one.</param>
[WirePayload(WireTypes.PaneFrame)]
public sealed record PaneFrame(string Frame);
