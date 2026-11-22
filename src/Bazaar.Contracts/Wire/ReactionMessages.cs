namespace Bazaar.Contracts.Wire;

/// <summary>
/// Request <c>gc.react</c>: adds or removes one emoji reaction on a message (docs/design/02, "Reactions on the wire"; log rules in
/// docs/design/01, "Reactions"). The envelope <c>id</c> is the idempotency key.
/// </summary>
/// <param name="GcId">The group chat.</param>
/// <param name="TargetN">The <c>n</c> of the message reacted to.</param>
/// <param name="Emoji">One emoji (a single grapheme cluster).</param>
/// <param name="Add">True to add the reaction, false to remove the sender's own.</param>
public sealed record GcReact(Guid GcId, long TargetN, string Emoji, bool Add);

/// <summary>Response to <c>gc.react</c>.</summary>
/// <param name="N">The seq of the reaction event written, or of the event already in effect; 0 when a remove found nothing.</param>
/// <param name="Changed">True when an event was written; false when the request was a no-op.</param>
public sealed record GcReactResult(long N, bool Changed);

/// <summary>Request <c>gc.reactions</c>: the folded reaction state of a GC.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="AfterN">When set, only messages with <c>n</c> above this; null for every message.</param>
public sealed record GcReactionsRequest(Guid GcId, long? AfterN);

/// <summary>One emoji on one message, with everyone who has it now.</summary>
/// <param name="TargetN">The message's <c>n</c>.</param>
/// <param name="Emoji">The emoji.</param>
/// <param name="By">Principals whose reaction is in effect, in the order they reacted.</param>
public sealed record WireReactionGroup(long TargetN, string Emoji, IReadOnlyList<Guid> By);

/// <summary>Response to <c>gc.reactions</c>.</summary>
/// <param name="Items">Groups ordered by message, then by when the emoji first appeared.</param>
public sealed record GcReactionsReply(IReadOnlyList<WireReactionGroup> Items);

/// <summary>Event <c>gc.reaction</c> on <c>gc:&lt;id&gt;</c>: one reaction changed. The envelope <c>n</c> is the reaction event's seq. Live only.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="TargetN">The message's <c>n</c>.</param>
/// <param name="Emoji">The emoji.</param>
/// <param name="By">Who reacted or removed.</param>
/// <param name="Add">True for an added reaction, false for a removed one.</param>
/// <param name="Ts">When the server wrote the event.</param>
public sealed record WireGcReaction(Guid GcId, long TargetN, string Emoji, Guid By, bool Add, DateTimeOffset Ts);

/// <summary>Request <c>gc.typing</c>: the sender starts, refreshes or stops typing. Ephemeral; never written to the log.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="Typing">True to start or refresh, false to stop.</param>
public sealed record GcTyping(Guid GcId, bool Typing);

/// <summary>Response to <c>gc.typing</c>.</summary>
/// <param name="Accepted">False when the signal was dropped for arriving inside the minimum interval, or because typing is off.</param>
public sealed record GcTypingResult(bool Accepted);

/// <summary>Kinds of an ephemeral <c>presence.update</c> delta.</summary>
public static class PresenceKinds
{
    /// <summary>A member is typing in a GC.</summary>
    public const string Typing = "typing";

    /// <summary>An agent's presence row changed (docs/design/23, "Sequence numbers on deltas").</summary>
    public const string Agent = "agent";
}

/// <summary>
/// Event <c>presence.update</c> (stream <c>presence</c>) with kind <see cref="PresenceKinds.Typing"/>. Sent only to connections
/// subscribed to the GC's stream, so the GC's access rules already applied. Carries no text.
/// </summary>
/// <param name="Kind">The delta kind; <see cref="PresenceKinds.Typing"/> for this payload. Receivers ignore kinds they do not know.</param>
/// <param name="GcId">The group chat.</param>
/// <param name="PrincipalId">Who is typing.</param>
/// <param name="Typing">True while typing, false once they stopped.</param>
/// <param name="TtlMs">Milliseconds after which the receiver treats the signal as expired unless refreshed.</param>
[WirePayload]
public sealed record PresenceTyping(string Kind, Guid GcId, Guid PrincipalId, bool Typing, int TtlMs);
