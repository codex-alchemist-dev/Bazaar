namespace Bazaar.Server.Agents.Inbox;

/// <summary>
/// A pending inbox item, as far as priority and batching need it. One item is one <c>inbox_item</c> row.
/// </summary>
/// <param name="Id">The item's identifier; the final tie-break.</param>
/// <param name="AgentId">The agent the item is for.</param>
/// <param name="Kind">An <see cref="InboxKind"/> value.</param>
/// <param name="GcId">The GC the item came from, when it has one.</param>
/// <param name="GcSeq">The GC sequence number of the message behind the item, when it has one.</param>
/// <param name="DueAt">The earliest time the item may be acted on.</param>
public sealed record InboxCandidate(Guid Id, Guid AgentId, string Kind, Guid? GcId, long? GcSeq, DateTimeOffset DueAt);
