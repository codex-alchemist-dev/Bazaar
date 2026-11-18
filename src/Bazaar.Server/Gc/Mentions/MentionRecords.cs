using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Mentions;

/// <summary>
/// A <c>gc_mention</c> row for the writer to persist (docs/design/01-gc-data-model.md).
/// </summary>
/// <param name="GcId">The GC the message is in.</param>
/// <param name="Seq">Sequence number of the message event.</param>
/// <param name="TargetPrincipalId">The mentioned principal.</param>
public sealed record GcMentionRow(Guid GcId, long Seq, Guid TargetPrincipalId);
