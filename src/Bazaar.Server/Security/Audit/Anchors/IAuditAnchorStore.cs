using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// Stores anchor receipts against the checkpoint they cover. Implemented by <c>Db.PostgresAuditAnchorStore</c>. The checkpoint is
/// named by its content (last sequence, chain head and Merkle root), so callers never need a database id.
/// </summary>
public interface IAuditAnchorStore
{
    /// <summary>Records one receipt. Throws when no stored checkpoint matches, or when the target already has a receipt for it.</summary>
    Task AppendAsync(AuditCheckpoint checkpoint, AuditAnchorReceipt receipt, CancellationToken ct = default);

    /// <summary>The receipts recorded for a checkpoint, ordered by target id. Empty when none exist.</summary>
    Task<IReadOnlyList<AuditAnchorReceipt>> ReadForCheckpointAsync(AuditCheckpoint checkpoint, CancellationToken ct = default);
}
