using Bazaar.Server.Security.Audit.Anchors;
using Bazaar.Server.Security.Audit.Checkpoints;
using Npgsql;

namespace Bazaar.Server.Security.Audit.Db;

/// <summary>
/// Writes and reads the <c>audit_checkpoint_anchor</c> table (migration audit/0002, design doc 48 section 5.3). A receipt is
/// matched to its checkpoint by last sequence, chain head and Merkle root, so a receipt can only name a checkpoint that is stored.
/// </summary>
/// <remarks>
/// Works under the runtime role (<c>bazaar_app</c>), which has INSERT and SELECT only. Verifying a receipt is the target's job
/// (<see cref="IAuditAnchorTarget"/>); this class checks shape and the checkpoint match, not authenticity.
/// </remarks>
public sealed class PostgresAuditAnchorStore(NpgsqlDataSource dataSource) : IAuditAnchorStore
{
    private const string _columns = "a.target_id, a.receipt, a.anchored_at";

    /// <summary>Inserts the receipt only when a stored checkpoint matches. Zero rows affected means no such checkpoint.</summary>
    private const string _insertSql = """
        INSERT INTO audit_checkpoint_anchor (checkpoint_id, target_id, receipt, anchored_at)
        SELECT c.checkpoint_id, $1, $2, $3
        FROM audit_checkpoint c
        WHERE c.last_seq = $4 AND c.chain_head_hash = $5 AND c.merkle_root = $6
        """;

    /// <inheritdoc />
    public async Task AppendAsync(AuditCheckpoint checkpoint, AuditAnchorReceipt receipt, CancellationToken ct = default)
    {
        Validate(checkpoint, receipt);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await InsertAsync(conn, tx, checkpoint, receipt, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the receipt on <paramref name="conn"/> inside the caller's <paramref name="tx"/>. Nothing is visible to other sessions
    /// until the caller commits, so the receipt and a related audit row commit or roll back together. The caller owns the transaction.
    /// </summary>
    public static Task AppendInTransactionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, AuditCheckpoint checkpoint, AuditAnchorReceipt receipt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        Validate(checkpoint, receipt);
        return InsertAsync(conn, tx, checkpoint, receipt, ct);
    }

    private static void Validate(AuditCheckpoint checkpoint, AuditAnchorReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateCheckpoint(checkpoint);
        ArgumentException.ThrowIfNullOrEmpty(receipt.TargetId);
        ArgumentNullException.ThrowIfNull(receipt.ReceiptBytes);
        if (receipt.ReceiptBytes.Length == 0)
            throw new ArgumentException("A receipt must carry bytes.", nameof(receipt));
        // Postgres keeps microseconds. A receipt that is not already aligned would be cut on insert, and the stored value would
        // differ from the one the target returned.
        if (receipt.AnchoredAt.ToUniversalTime().UtcTicks % 10 != 0)
            throw new ArgumentException("Receipt AnchoredAt must be aligned to whole microseconds.", nameof(receipt));
    }

    private static async Task InsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, AuditCheckpoint checkpoint, AuditAnchorReceipt receipt, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand(_insertSql, conn, tx);
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = receipt.TargetId });
        insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = receipt.ReceiptBytes });
        insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = receipt.AnchoredAt });
        insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.LastSeq });
        insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = checkpoint.ChainHeadHash });
        insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = checkpoint.MerkleRoot });

        var affected = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (affected != 1)
            throw new InvalidOperationException($"No stored checkpoint matches the anchor for last seq {checkpoint.LastSeq}.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditAnchorReceipt>> ReadForCheckpointAsync(AuditCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateCheckpoint(checkpoint);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {_columns}
            FROM audit_checkpoint_anchor a
            JOIN audit_checkpoint c ON c.checkpoint_id = a.checkpoint_id
            WHERE c.last_seq = $1 AND c.chain_head_hash = $2 AND c.merkle_root = $3
            ORDER BY a.target_id
            """,
            conn);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.LastSeq });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = checkpoint.ChainHeadHash });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = checkpoint.MerkleRoot });

        var receipts = new List<AuditAnchorReceipt>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            receipts.Add(new AuditAnchorReceipt(
                reader.GetString(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return receipts;
    }

    private static void ValidateCheckpoint(AuditCheckpoint checkpoint)
    {
        if (checkpoint.LastSeq < 1)
            throw new ArgumentException("Checkpoint last seq must be 1 or later.", nameof(checkpoint));
        if (checkpoint.ChainHeadHash is not { Length: 32 } || checkpoint.MerkleRoot is not { Length: 32 })
            throw new ArgumentException("Checkpoint hashes must be 32 bytes.", nameof(checkpoint));
    }
}
