using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Mentions;

/// <summary>Why an inbox row exists. Only mentions are planned here; other reasons belong to other planners.</summary>
public enum InboxReason
{
    /// <summary>The principal was mentioned in a GC message.</summary>
    Mention,
}

/// <summary>
/// An inbox row for a principal (docs/design/06-agent-runtime.md, <c>inbox_item</c>). The writer
/// assigns the row's id, state and priority.
/// </summary>
/// <param name="PrincipalId">The principal whose inbox receives the row.</param>
/// <param name="GcId">The GC the message is in.</param>
/// <param name="Seq">Sequence number of the message event.</param>
/// <param name="Reason">Why the row exists.</param>
public sealed record InboxRow(Guid PrincipalId, Guid GcId, long Seq, InboxReason Reason);

/// <summary>
/// A <c>gc_mention</c> row for the writer to persist (docs/design/01-gc-data-model.md).
/// </summary>
/// <param name="GcId">The GC the message is in.</param>
/// <param name="Seq">Sequence number of the message event.</param>
/// <param name="TargetPrincipalId">The mentioned principal.</param>
public sealed record GcMentionRow(Guid GcId, long Seq, Guid TargetPrincipalId);
