namespace Bazaar.Server.Portability;

/// <summary>What the first mismatch in a replay was.</summary>
public enum AuditReplayFailureKind
{
    /// <summary>A row is missing, duplicated or out of order (seq is not the previous seq plus one).</summary>
    SequenceBroken,

    /// <summary>A row's <c>prev_hash</c> is not the previous row's hash (or the starting checkpoint's head).</summary>
    PrevHashMismatch,

    /// <summary>A row's hash does not match its fields.</summary>
    HashMismatch,

    /// <summary>A hash or signature field is missing or has the wrong length.</summary>
    MalformedInput,

    /// <summary>A checkpoint's range or row count does not fit the rows or the previous checkpoint.</summary>
    CheckpointRangeBroken,

    /// <summary>A checkpoint's chain head is not the hash of the row at its last seq.</summary>
    CheckpointHeadMismatch,

    /// <summary>A checkpoint's Merkle root is not the root over its rows.</summary>
    CheckpointMerkleMismatch,

    /// <summary>A checkpoint's signature does not verify.</summary>
    CheckpointSignatureInvalid,

    /// <summary>The rows end before a checkpoint's last seq.</summary>
    Truncated,
}

/// <summary>The first mismatch: the seq it was found at, what kind it was, and a short fixed description.</summary>
public sealed record AuditReplayFailure(long Seq, AuditReplayFailureKind Kind, string Detail);

/// <summary>
/// Result of a replay. <see cref="UncheckpointedTailRows"/> counts verified rows after the last checkpoint: their links
/// hold, but no signed checkpoint covers them yet.
/// </summary>
public sealed record AuditReplayResult(
    bool IsValid,
    long RowsChecked,
    long CheckpointsChecked,
    long UncheckpointedTailRows,
    AuditReplayFailure? FirstMismatch)
{
    /// <summary>A failed result.</summary>
    public static AuditReplayResult Fail(long rows, long checkpoints, AuditReplayFailure failure) =>
        new(false, rows, checkpoints, 0, failure);
}

/// <summary>
/// Pure offline replay of an exported audit chain (doc 48 section 5.2: recompute the chain from a starting checkpoint and
/// report the first mismatch). No I/O and no instance. Anything that does not fit is a failure; it never passes by default.
/// </summary>
public static class AuditChainReplay
{
    /// <summary>
    /// Replays <paramref name="rows"/> against <paramref name="checkpoints"/>.
    /// With <paramref name="startsAtGenesis"/>, rows start at seq 1 with the genesis <c>prev_hash</c> and every checkpoint
    /// is a following one. Otherwise the first checkpoint is the trusted starting point: its signature is checked, rows
    /// start at its last seq plus one, and the first row's <c>prev_hash</c> must be its chain head. The remaining
    /// checkpoints must be contiguous. Each checkpoint is checked for range, head, Merkle root and signature in that order.
    /// </summary>
    public static AuditReplayResult Replay(
        IEnumerable<AuditRow> rows,
        IReadOnlyList<AuditCheckpointRecord> checkpoints,
        IAuditSignatureVerifier verifier,
        bool startsAtGenesis)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(verifier);

        byte[] prevHash;
        long expectedSeq;
        var nextCheckpoint = 0;
        long checkpointsChecked = 0;

        if (startsAtGenesis)
        {
            prevHash = AuditHashing.GenesisHash();
            expectedSeq = 1;
        }
        else
        {
            if (checkpoints.Count == 0)
            {
                return AuditReplayResult.Fail(0, 0, new AuditReplayFailure(0, AuditReplayFailureKind.Truncated, "The starting checkpoint is missing."));
            }

            var start = checkpoints[0];
            var startProblem = CheckShape(start, start.FirstSeq)
                ?? CheckSignature(start, verifier);
            if (startProblem is not null)
            {
                return AuditReplayResult.Fail(0, 0, startProblem);
            }

            prevHash = start.ChainHeadHash;
            expectedSeq = start.LastSeq + 1;
            nextCheckpoint = 1;
            checkpointsChecked = 1;
        }

        long rowsChecked = 0;
        var segment = new List<byte[]>();
        long segmentStart = expectedSeq;

        foreach (var row in rows)
        {
            if (row is null || row.Actor is null || row.Kind is null || row.Payload is null
                || row.PrevHash is not { Length: AuditHashing.HashSize } || row.Hash is not { Length: AuditHashing.HashSize })
            {
                return AuditReplayResult.Fail(rowsChecked, checkpointsChecked, new AuditReplayFailure(
                    row?.Seq ?? expectedSeq, AuditReplayFailureKind.MalformedInput, "A row is missing a field or has a hash of the wrong length."));
            }

            if (row.Seq != expectedSeq)
            {
                return AuditReplayResult.Fail(rowsChecked, checkpointsChecked, new AuditReplayFailure(
                    row.Seq, AuditReplayFailureKind.SequenceBroken, "A row is missing, repeated or out of order."));
            }

            if (!AuditHashing.HashesEqual(row.PrevHash, prevHash))
            {
                return AuditReplayResult.Fail(rowsChecked, checkpointsChecked, new AuditReplayFailure(
                    row.Seq, AuditReplayFailureKind.PrevHashMismatch, "The row does not continue the previous hash."));
            }

            if (!AuditHashing.HashesEqual(AuditHashing.ComputeRowHash(row), row.Hash))
            {
                return AuditReplayResult.Fail(rowsChecked, checkpointsChecked, new AuditReplayFailure(
                    row.Seq, AuditReplayFailureKind.HashMismatch, "The row hash does not match its fields."));
            }

            if (nextCheckpoint < checkpoints.Count && checkpoints[nextCheckpoint].LastSeq < row.Seq)
            {
                return AuditReplayResult.Fail(rowsChecked, checkpointsChecked, new AuditReplayFailure(
                    checkpoints[nextCheckpoint].LastSeq, AuditReplayFailureKind.CheckpointRangeBroken, "A checkpoint ends before the rows it should cover."));
            }

            rowsChecked++;
            prevHash = row.Hash;
            expectedSeq++;
            segment.Add(row.Hash);

            if (nextCheckpoint < checkpoints.Count && checkpoints[nextCheckpoint].LastSeq == row.Seq)
            {
                var checkpoint = checkpoints[nextCheckpoint];
                var problem = CheckShape(checkpoint, segmentStart)
                    ?? CheckHeadAndRoot(checkpoint, row.Hash, segment)
                    ?? CheckSignature(checkpoint, verifier);
                if (problem is not null)
                {
                    return AuditReplayResult.Fail(rowsChecked, checkpointsChecked, problem);
                }

                nextCheckpoint++;
                checkpointsChecked++;
                segment.Clear();
                segmentStart = expectedSeq;
            }
        }

        if (nextCheckpoint < checkpoints.Count)
        {
            return AuditReplayResult.Fail(rowsChecked, checkpointsChecked, new AuditReplayFailure(
                checkpoints[nextCheckpoint].LastSeq, AuditReplayFailureKind.Truncated, "The rows end before a checkpoint's last seq."));
        }

        return new AuditReplayResult(true, rowsChecked, checkpointsChecked, segment.Count, null);
    }

    /// <summary>Range checks: the checkpoint starts where its rows start, and its count matches its range.</summary>
    private static AuditReplayFailure? CheckShape(AuditCheckpointRecord checkpoint, long expectedFirstSeq)
    {
        if (checkpoint.ChainHeadHash is not { Length: AuditHashing.HashSize }
            || checkpoint.MerkleRoot is not { Length: AuditHashing.HashSize }
            || checkpoint.Signature is null || checkpoint.KeyId is null)
        {
            return new AuditReplayFailure(checkpoint.LastSeq, AuditReplayFailureKind.MalformedInput, "A checkpoint is missing a field or has a hash of the wrong length.");
        }

        if (checkpoint.FirstSeq < 1 || checkpoint.LastSeq < checkpoint.FirstSeq
            || checkpoint.LastSeq - checkpoint.FirstSeq + 1 != checkpoint.RowCount
            || checkpoint.FirstSeq != expectedFirstSeq)
        {
            return new AuditReplayFailure(checkpoint.LastSeq, AuditReplayFailureKind.CheckpointRangeBroken, "A checkpoint's range or row count does not fit its rows.");
        }

        return null;
    }

    private static AuditReplayFailure? CheckHeadAndRoot(AuditCheckpointRecord checkpoint, byte[] lastRowHash, List<byte[]> segment)
    {
        if (!AuditHashing.HashesEqual(checkpoint.ChainHeadHash, lastRowHash))
        {
            return new AuditReplayFailure(checkpoint.LastSeq, AuditReplayFailureKind.CheckpointHeadMismatch, "A checkpoint's chain head is not the hash of its last row.");
        }

        if (!AuditHashing.HashesEqual(checkpoint.MerkleRoot, AuditMerkle.ComputeRoot(segment)))
        {
            return new AuditReplayFailure(checkpoint.LastSeq, AuditReplayFailureKind.CheckpointMerkleMismatch, "A checkpoint's Merkle root is not the root over its rows.");
        }

        return null;
    }

    private static AuditReplayFailure? CheckSignature(AuditCheckpointRecord checkpoint, IAuditSignatureVerifier verifier) =>
        verifier.Verify(AuditHashing.CheckpointSignedBytes(checkpoint), checkpoint.Signature)
            ? null
            : new AuditReplayFailure(checkpoint.LastSeq, AuditReplayFailureKind.CheckpointSignatureInvalid, "A checkpoint's signature does not verify.");
}
