namespace Bazaar.Contracts.Wire;

/// <summary>Payload of <c>subscribe</c>.</summary>
/// <param name="Streams">Streams with the cursor to resume from.</param>
public sealed record Subscribe(IReadOnlyList<StreamCursor> Streams);

/// <summary>Payload of <c>unsubscribe</c>.</summary>
/// <param name="Streams">Stream names to drop.</param>
public sealed record Unsubscribe(IReadOnlyList<string> Streams);

/// <summary>Outcome for one stream of a <c>subscribe</c>.</summary>
/// <param name="S">Stream name.</param>
/// <param name="Status">One of <see cref="SubscribeStatuses"/>.</param>
public sealed record StreamResult(string S, string Status);

/// <summary>Response payload of <c>subscribe</c>.</summary>
/// <param name="Results">One entry per requested stream.</param>
public sealed record SubscribeResult(IReadOnlyList<StreamResult> Results);

/// <summary>Event: replay gap too large (<c>wire.replay_cutoff_entries</c>); the client pages back with <c>gc.history</c>.</summary>
/// <param name="S">Stream name.</param>
/// <param name="LatestN">Latest <c>n</c> on the server.</param>
/// <param name="LatestEv">Latest <c>ev</c> on the server.</param>
/// <param name="Unread">Unread count for the viewer.</param>
/// <param name="Tail">The last entries of the stream, oldest first.</param>
public sealed record Resync(string S, long LatestN, long LatestEv, int Unread, IReadOnlyList<WireGcMessage> Tail);

/// <summary>Event: the connection's queue overflowed; the client resubscribes from its cursor.</summary>
/// <param name="S">Stream name.</param>
/// <param name="LastSent">Last <c>n</c> sent before live delivery stopped.</param>
public sealed record Lagged(string S, long LastSent);

/// <summary>Event: access to a stream ended.</summary>
/// <param name="S">Stream name.</param>
public sealed record StreamRevoked(string S);

/// <summary>Application-level keep-alive.</summary>
/// <param name="Nonce">Echoed in the pong, or null.</param>
public sealed record Ping(string? Nonce);

/// <summary>Answer to <see cref="Ping"/>.</summary>
/// <param name="Nonce">The ping's nonce, or null.</param>
public sealed record Pong(string? Nonce);

/// <summary>Payload of an <c>err</c> envelope.</summary>
/// <param name="Code">One of <see cref="WireErrorCodes"/>, or a code a newer server added.</param>
/// <param name="Message">Human-readable text; never contains payload data.</param>
/// <param name="RetryAfterMs">With <c>rate_limited</c>: milliseconds to wait.</param>
public sealed record WireError(string Code, string? Message, int? RetryAfterMs);
