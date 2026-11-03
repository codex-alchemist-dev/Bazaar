using Bazaar.Server.Agents.Inbox;

namespace Bazaar.Server.Skimmer.Digest;

/// <summary>
/// One <c>inbox_item</c> row to insert, as planned from a gate decision (doc 13, "Digest").
/// </summary>
/// <param name="AgentId">The agent the item is for.</param>
/// <param name="Kind">
/// <see cref="InboxKind.GcDigest"/> for a deferred message, or <see cref="InboxKind.SkimmerPromoted"/>
/// for a scored promotion.
/// </param>
/// <param name="GcId">The GC of the message.</param>
/// <param name="Seq">The GC sequence number of the message.</param>
/// <param name="DueAt">The earliest time the item may be acted on.</param>
/// <param name="PayloadJson">The JSON payload; never contains message text.</param>
/// <param name="IdempotencyKey">See <see cref="ForKey"/>.</param>
public sealed record DigestEnqueue(
    Guid AgentId,
    string Kind,
    Guid GcId,
    long Seq,
    DateTimeOffset DueAt,
    string PayloadJson,
    string IdempotencyKey)
{
    /// <summary>
    /// Builds the idempotency key for one inbox row. The key is the text form of the unique key that doc 06
    /// requires on <c>inbox_item</c>: <c>(agent_id, gc_id, gc_seq, kind)</c>. A duplicate enqueue with the same
    /// key is a no-op. Format: <c>&lt;agentId:N&gt;|&lt;gcId:N&gt;|&lt;seq&gt;|&lt;kind&gt;</c>.
    /// </summary>
    public static string ForKey(Guid agentId, Guid gcId, long seq, string kind) =>
        $"{agentId:N}|{gcId:N}|{seq}|{kind}";
}
