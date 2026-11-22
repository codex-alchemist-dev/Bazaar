namespace Bazaar.Contracts.Wire;

/// <summary>
/// Request <c>gc.list</c> [PROPOSED, doc 02, GC list]: the GCs the caller is an active member of. Only the caller's own
/// memberships are ever listed; archived GCs are not.
/// </summary>
/// <param name="Limit">Most items to return, 1 to 200.</param>
/// <param name="IncludeThinking">True to list the caller's Thinking GCs too (kind <c>thinking</c>); false leaves them out.</param>
[WirePayload(WireTypes.GcList)]
public sealed record GcListRequest(int Limit, bool IncludeThinking = false);

/// <summary>One GC in a <c>gc.list</c> answer.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="Title">The GC's stored title.</param>
/// <param name="Kind">The GC's type key as stored, such as <c>general</c>, <c>dm</c> or <c>thinking</c>.</param>
/// <param name="MemberCount">Active members.</param>
/// <param name="HeadSeq">The GC's last assigned sequence number.</param>
/// <param name="LastActivityTs">When the GC's last log entry was written; null for a GC with no entries.</param>
[WirePayload]
public sealed record GcListItem(Guid GcId, string Title, string Kind, int MemberCount, long HeadSeq, DateTimeOffset? LastActivityTs);

/// <summary>Response to <c>gc.list</c>.</summary>
/// <param name="Items">The caller's GCs, most recently active first.</param>
/// <param name="HasMore">True when the caller is in more GCs than <c>Limit</c> allowed.</param>
/// <param name="DefaultGcId">The GC a client should open by default under setting <c>gc.default_gc_rule</c>, or null when none qualifies.</param>
[WirePayload]
public sealed record GcListReply(IReadOnlyList<GcListItem> Items, bool HasMore, Guid? DefaultGcId);
