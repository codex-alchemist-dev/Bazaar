using System.Runtime.CompilerServices;
using Bazaar.Server.Security.Audit.Checkpoints;
using Npgsql;

namespace Bazaar.Server.Security.Audit.Db;

/// <summary>
/// A checkpoint as stored: the checkpoint record, the signing key identifier and the signature over
/// <see cref="AuditCheckpoint.SignedBytes"/>. The store never signs and never holds a key.
/// </summary>
public sealed record StoredAuditCheckpoint(AuditCheckpoint Checkpoint, string KeyId, byte[] Signature);

/// <summary>
/// Writes and reads the <c>audit_checkpoint</c> table (migration audit/0001, design doc 48 section 5.2). The writer enforces the
/// rules that make the checkpoints a usable chain of their own: each checkpoint covers the rows right after the previous
/// one, and its chain head must match the <c>audit_log</c> row at its last sequence number. Both checks run in the same
/// transaction as the insert, under an advisory lock, so two writers cannot interleave.
/// </summary>
/// <remarks>
/// Works under the runtime role (<c>bazaar_app</c>), which has INSERT and SELECT only. Signature checks are the caller's
/// job (<see cref="IAuditCheckpointSigner"/>); this class checks shape and chain consistency, not authenticity.
/// </remarks>
public sealed class PostgresAuditCheckpointStore(NpgsqlDataSource dataSource)
{
    /// <summary>Advisory-lock key for checkpoint appends. Distinct from the audit append lock ("BAUDITLG").</summary>
    private const long _appendLockKey = 0x4241554443504B54;

    private const string _columns = "first_seq, last_seq, row_count, chain_head_hash, merkle_root, created_at, key_id, signature";

    /// <summary>
    /// Appends one checkpoint. Throws <see cref="InvalidOperationException"/> when it does not continue the stored chain of
    /// checkpoints, or when its chain head does not match the audit log at its last sequence number.
    /// </summary>
    public async Task AppendAsync(StoredAuditCheckpoint stored, CancellationToken ct = default)
    {
        ValidateStored(stored);
        var cp = stored.Checkpoint;

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = _appendLockKey });
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var latestLast = await ReadLatestLastSeqAsync(conn, tx, ct).ConfigureAwait(false);
        var expectedFirst = latestLast is null ? 1L : latestLast.Value + 1;
        if (cp.FirstSeq != expectedFirst)
            throw new InvalidOperationException(
                $"Checkpoint must start at seq {expectedFirst} to continue the chain of checkpoints, not {cp.FirstSeq}.");

        // The insert succeeds only when the audit log holds the head row at LastSeq, so a checkpoint cannot name a head the
        // chain does not contain. Zero rows affected means the head did not match.
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO audit_checkpoint (first_seq, last_seq, row_count, chain_head_hash, merkle_root, signature, key_id, created_at)
            SELECT $1, $2, $3, $4, $5, $6, $7, $8
            WHERE EXISTS (SELECT 1 FROM audit_log WHERE seq = $2 AND hash = $4)
            """,
            conn,
            tx))
        {
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cp.FirstSeq });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cp.LastSeq });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cp.RowCount });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = cp.ChainHeadHash });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = cp.MerkleRoot });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = stored.Signature });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = stored.KeyId });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(cp.CreatedAt) });

            var affected = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected != 1)
                throw new InvalidOperationException(
                    $"Checkpoint chain head does not match the audit log at seq {cp.LastSeq}.");
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The newest checkpoint, or null when none has been written.</summary>
    public async Task<StoredAuditCheckpoint?> ReadLatestAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"SELECT {_columns} FROM audit_checkpoint ORDER BY last_seq DESC LIMIT 1", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Every checkpoint in sequence order. One statement, so the result is a consistent snapshot.</summary>
    public async IAsyncEnumerable<StoredAuditCheckpoint> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"SELECT {_columns} FROM audit_checkpoint ORDER BY last_seq", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            yield return Map(reader);
    }

    private static async Task<long?> ReadLatestLastSeqAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT last_seq FROM audit_checkpoint ORDER BY last_seq DESC LIMIT 1", conn, tx);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? null : (long)value;
    }

    private static void ValidateStored(StoredAuditCheckpoint stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(stored.Checkpoint);
        ArgumentException.ThrowIfNullOrEmpty(stored.KeyId);
        ArgumentNullException.ThrowIfNull(stored.Signature);
        if (stored.Signature.Length == 0)
            throw new ArgumentException("A checkpoint must carry a signature.", nameof(stored));

        var cp = stored.Checkpoint;
        if (cp.FirstSeq < 1 || cp.LastSeq < cp.FirstSeq)
            throw new ArgumentException("Checkpoint range must start at 1 or later and end at or after its start.", nameof(stored));
        if (cp.RowCount != cp.LastSeq - cp.FirstSeq + 1)
            throw new ArgumentException("Checkpoint row count must equal the number of sequence numbers it covers.", nameof(stored));
        if (cp.ChainHeadHash is not { Length: 32 } || cp.MerkleRoot is not { Length: 32 })
            throw new ArgumentException("Checkpoint hashes must be 32 bytes.", nameof(stored));
        // The signature covers CreatedAt in ticks, and Postgres keeps microseconds. A checkpoint that is not already aligned
        // would sign different bytes from the ones a verifier reads back, so it is rejected instead of silently cut.
        if (cp.CreatedAt.ToUniversalTime().UtcTicks % 10 != 0)
            throw new ArgumentException("Checkpoint CreatedAt must be aligned to whole microseconds before signing.", nameof(stored));
    }

    /// <summary>Postgres stores microseconds, so the checkpoint is cut to them before insert (as the audit log does).</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    private static StoredAuditCheckpoint Map(NpgsqlDataReader reader)
    {
        var checkpoint = new AuditCheckpoint(
            FirstSeq: reader.GetInt64(0),
            LastSeq: reader.GetInt64(1),
            RowCount: reader.GetInt64(2),
            ChainHeadHash: reader.GetFieldValue<byte[]>(3),
            MerkleRoot: reader.GetFieldValue<byte[]>(4),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(5));
        return new StoredAuditCheckpoint(checkpoint, reader.GetString(6), reader.GetFieldValue<byte[]>(7));
    }
}
