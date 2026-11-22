namespace Bazaar.Server.Portability;

/// <summary>
/// GC side of a withdrawal (doc 44 section 8.8, PROPOSED). An implementation adds the <c>withdrawn_by_import</c> version through the
/// GC log writer and message editor, never by SQL on <c>gc_message_version</c>. It must be idempotent for a repeated batch, because
/// the withdrawal record is written after this call returns.
/// </summary>
public interface IGcWithdrawalMarker
{
    /// <summary>Marks one batch of GC entries as withdrawn by the job. Throws if the batch cannot be written.</summary>
    Task MarkWithdrawnAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct);
}

/// <summary>
/// Vault side of a withdrawal: one new git commit that restores the prior state of each path. Implemented outside the Postgres
/// project. It must be idempotent for a repeated batch.
/// </summary>
public interface IVaultWithdrawalReverter
{
    /// <summary>Reverts one batch of Vault files. Throws if the batch cannot be written.</summary>
    Task RevertAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct);
}
