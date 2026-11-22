using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// Opens one transaction in which an anchor receipt and its audit events commit or roll back together. Implemented by
/// <c>Db.PostgresAuditAnchorAtomicWriter</c>. <see cref="AuditAnchorService"/> uses it when given one; without it the receipt and the
/// <c>audit.anchor.recovered</c> event are two separate writes (in-memory test doubles only).
/// </summary>
public interface IAuditAnchorAtomicWriter
{
    /// <summary>Begins a transaction. Disposing the scope without <see cref="IAuditAnchorAtomicScope.CommitAsync"/> rolls it back.</summary>
    Task<IAuditAnchorAtomicScope> BeginAsync(CancellationToken ct = default);
}

/// <summary>One open transaction. Nothing it writes is visible to other sessions until <see cref="CommitAsync"/> succeeds.</summary>
public interface IAuditAnchorAtomicScope : IAsyncDisposable
{
    /// <summary>Writes the receipt inside the transaction.</summary>
    Task AppendReceiptAsync(AuditCheckpoint checkpoint, AuditAnchorReceipt receipt, CancellationToken ct = default);

    /// <summary>True when an <c>audit.anchor.failed</c> row exists for this checkpoint and target, read inside the transaction.</summary>
    Task<bool> HasRecordedFailureAsync(long lastSeq, string targetId, CancellationToken ct = default);

    /// <summary>Appends an audit entry inside the transaction.</summary>
    Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>Commits the receipt and the audit entries together.</summary>
    Task CommitAsync(CancellationToken ct = default);
}
