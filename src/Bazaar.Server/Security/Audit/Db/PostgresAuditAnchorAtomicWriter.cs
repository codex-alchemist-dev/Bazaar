using Bazaar.Server.Security.Audit.Anchors;
using Bazaar.Server.Security.Audit.Checkpoints;
using Npgsql;

namespace Bazaar.Server.Security.Audit.Db;

/// <summary>
/// Writes an anchor receipt and its audit events in one PostgreSQL transaction. The receipt goes through
/// <see cref="PostgresAuditAnchorStore.AppendInTransactionAsync"/>, the events through
/// <see cref="ITransactionalAuditLog.AppendInTransactionAsync"/> (same advisory lock as the chain), and the failure check through
/// <see cref="PostgresAuditAnchorFailureLog.HasRecordedFailureInTransactionAsync"/>.
/// </summary>
public sealed class PostgresAuditAnchorAtomicWriter(
    NpgsqlDataSource dataSource,
    ITransactionalAuditLog auditLog) : IAuditAnchorAtomicWriter
{
    /// <inheritdoc />
    public async Task<IAuditAnchorAtomicScope> BeginAsync(CancellationToken ct = default)
    {
        var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            return new Scope(conn, tx, auditLog);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Scope(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        ITransactionalAuditLog auditLog) : IAuditAnchorAtomicScope
    {
        public Task AppendReceiptAsync(AuditCheckpoint checkpoint, AuditAnchorReceipt receipt, CancellationToken ct = default) =>
            PostgresAuditAnchorStore.AppendInTransactionAsync(conn, tx, checkpoint, receipt, ct);

        public Task<bool> HasRecordedFailureAsync(long lastSeq, string targetId, CancellationToken ct = default) =>
            PostgresAuditAnchorFailureLog.HasRecordedFailureInTransactionAsync(conn, tx, lastSeq, targetId, ct);

        public Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default) =>
            auditLog.AppendInTransactionAsync(conn, tx, entry, ct);

        public Task CommitAsync(CancellationToken ct = default) => tx.CommitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            // Disposing an uncommitted transaction rolls it back.
            await tx.DisposeAsync().ConfigureAwait(false);
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }
}
