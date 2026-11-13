using System.Security.Cryptography;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Components.Backup.Restore;

/// <summary>The first check that failed in <see cref="RestoreAuditVerifier.Verify"/>, or <see cref="Ok"/>.</summary>
public enum RestoreAuditReason
{
    Ok,
    SignatureInvalid,
    LinkBroken,
    SequenceBroken,
    HashMismatch,
    RootMismatch,
}

/// <summary>
/// Result of <see cref="RestoreAuditVerifier.Verify"/>. <see cref="RowsChecked"/> counts the rows that passed before the
/// failure (all rows when <see cref="Reason"/> is Ok). <see cref="FailedSeq"/> is the row that failed, when a row did.
/// </summary>
public sealed record RestoreAuditVerification(RestoreAuditReason Reason, int RowsChecked, long? FailedSeq)
{
    public bool IsValid => Reason == RestoreAuditReason.Ok;
}

/// <summary>
/// Verifies the audit chain from the last anchored checkpoint during a restore (doc 48 section 6: the restore starts in
/// read-only mode after this check, and the owner confirms). Pure logic: no database and no Postgres I/O. The caller
/// records each step under the restore id.
/// </summary>
/// <remarks>
/// Steps run in order and stop at the first failure: (a) the checkpoint signature, (b) the first row after the anchor
/// links to the anchor (sequence and previous hash), (c) every row after the anchor (sequence, previous hash, hash
/// recomputed by <see cref="AuditChain.ComputeHash"/>), starting from the anchor head, (d) the Merkle root over the
/// row hashes after the anchor equals <see cref="AuditCheckpoint.MerkleRoot"/>.
/// Note for step (d): <see cref="AuditCheckpoint"/> defines its root over the rows since the previous checkpoint, which
/// are the rows the checkpoint covers, not the rows after it. Passing rows after the anchor therefore matches a root only
/// when the caller means that set. Which rows the root input takes is an open question for the main session.
/// </remarks>
public static class RestoreAuditVerifier
{
    public static RestoreAuditVerification Verify(
        AuditCheckpoint anchor,
        IReadOnlyList<AuditRecord> rowsAfterAnchor,
        IAuditCheckpointSigner signer,
        byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(rowsAfterAnchor);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(signature);

        // (a) The checkpoint signature covers the anchor fields, so a changed head or root fails here first.
        if (!signer.Verify(anchor.SignedBytes(), signature))
        {
            return new RestoreAuditVerification(RestoreAuditReason.SignatureInvalid, 0, null);
        }

        // (b) The first row after the anchor must continue the chain from the anchor head.
        if (rowsAfterAnchor.Count > 0)
        {
            var first = rowsAfterAnchor[0];
            if (first.Seq != anchor.LastSeq + 1)
            {
                return new RestoreAuditVerification(RestoreAuditReason.SequenceBroken, 0, first.Seq);
            }

            if (!first.PrevHash.AsSpan().SequenceEqual(anchor.ChainHeadHash))
            {
                return new RestoreAuditVerification(RestoreAuditReason.LinkBroken, 0, first.Seq);
            }
        }

        // (c) Walk every row from the anchor head. AuditChain.Verify starts from genesis, so the walk is repeated here.
        byte[] prevHash = anchor.ChainHeadHash;
        long expectedSeq = anchor.LastSeq + 1;
        for (var i = 0; i < rowsAfterAnchor.Count; i++)
        {
            var row = rowsAfterAnchor[i];
            if (row.Seq != expectedSeq)
            {
                return new RestoreAuditVerification(RestoreAuditReason.SequenceBroken, i, row.Seq);
            }

            if (!row.PrevHash.AsSpan().SequenceEqual(prevHash))
            {
                return new RestoreAuditVerification(RestoreAuditReason.LinkBroken, i, row.Seq);
            }

            var recomputed = AuditChain.ComputeHash(row.Seq, row.Ts, row.Actor, row.Kind, row.Payload, row.PrevHash);
            if (!row.Hash.AsSpan().SequenceEqual(recomputed))
            {
                return new RestoreAuditVerification(RestoreAuditReason.HashMismatch, i, row.Seq);
            }

            prevHash = row.Hash;
            expectedSeq++;
        }

        // (d) The Merkle root over the row hashes (leaf rule from MerkleTree). Every hash here matched step (c), so each
        // is 32 bytes and the tree builds. An empty list gives the empty-tree root.
        var root = new MerkleTree(rowsAfterAnchor.Select(r => r.Hash)).Root;
        if (!root.AsSpan().SequenceEqual(anchor.MerkleRoot))
        {
            return new RestoreAuditVerification(RestoreAuditReason.RootMismatch, rowsAfterAnchor.Count, null);
        }

        return new RestoreAuditVerification(RestoreAuditReason.Ok, rowsAfterAnchor.Count, null);
    }
}
