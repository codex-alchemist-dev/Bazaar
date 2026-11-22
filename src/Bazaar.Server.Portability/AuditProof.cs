using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>
/// An inclusion proof for one audit row against a signed checkpoint (doc 48 section 5.3): the leaf hash, the sibling path,
/// the root, and the checkpoint id, plus the checkpoint fields and signature so it verifies offline. Byte fields are Base64.
/// </summary>
public sealed record InclusionProofFile(
    string? Format,
    string? CheckpointId,
    long RowSeq,
    long LeafIndex,
    long TreeSize,
    byte[]? LeafHash,
    IReadOnlyList<byte[]>? Siblings,
    byte[]? Root,
    ProofCheckpoint? Checkpoint);

/// <summary>The checkpoint a proof is against, exactly as stored (signature untouched).</summary>
public sealed record ProofCheckpoint(
    long FirstSeq,
    long LastSeq,
    long RowCount,
    byte[]? ChainHeadHash,
    byte[]? MerkleRoot,
    DateTimeOffset CreatedAt,
    string? KeyId,
    byte[]? Signature);

/// <summary>Outcome of <see cref="AuditInclusionProofVerifier"/>. <see cref="Reason"/> is a fixed description.</summary>
public sealed record AuditProofVerification(bool IsValid, string Reason)
{
    /// <summary>The proof verified.</summary>
    public static AuditProofVerification Valid { get; } = new(true, "ok");

    /// <summary>The proof failed.</summary>
    public static AuditProofVerification Invalid(string reason) => new(false, reason);
}

/// <summary>Builds the proof file for one row against its checkpoint (doc 48 section 5.3).</summary>
public sealed class AuditProofExporter(IAuditLogReadSource source, IAuditSignatureVerifier verifier)
{
    /// <summary>Format id inside the proof file.</summary>
    public const string FormatId = "bazaar.audit.inclusion-proof.v1";

    /// <summary>Published path of the proof for a row of a checkpoint.</summary>
    public static string PathFor(AuditCheckpointRecord checkpoint, long seq) =>
        AuditArchiveFormat.ProofsFolder + checkpoint.Id + "-seq" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json";

    /// <summary>
    /// Reads the checkpoint that covers <paramref name="seq"/> and its rows, checks them (signature, row hashes, links, Merkle
    /// root), builds the proof and verifies the proof file before returning it. Fails closed with
    /// <see cref="AuditExportException"/>: a row not yet checkpointed, a bad signature or any mismatch yields no proof.
    /// </summary>
    public async Task<BundleFile> ExportAsync(long seq, CancellationToken ct = default)
    {
        var checkpoint = await source.ReadCheckpointCoveringAsync(seq, ct).ConfigureAwait(false)
            ?? throw new AuditExportException($"Row {seq} is not covered by a checkpoint yet, so no proof can be made.");

        if (seq < checkpoint.FirstSeq || seq > checkpoint.LastSeq)
        {
            throw new AuditExportException("The checkpoint the source returned does not cover the row.");
        }

        var rows = new List<AuditRow>();
        await foreach (var row in source.ReadRowsAsync(checkpoint.FirstSeq, checkpoint.LastSeq, ct).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        // The proof is only as good as the rows under it, so check them like a replay of this one checkpoint range.
        // The first row's prev_hash is not checked here: the root commits to the row hashes, which cover it.
        long expected = checkpoint.FirstSeq;
        foreach (var row in rows)
        {
            if (row.Seq != expected++ || row.Hash is not { Length: AuditHashing.HashSize }
                || !AuditHashing.HashesEqual(AuditHashing.ComputeRowHash(row), row.Hash))
            {
                throw new AuditExportException($"Row {row.Seq} in the checkpoint range does not verify.");
            }
        }

        if (rows.Count != checkpoint.RowCount || expected != checkpoint.LastSeq + 1)
        {
            throw new AuditExportException("The checkpoint range is not fully present.");
        }

        var hashes = rows.Select(r => r.Hash).ToList();
        if (!AuditHashing.HashesEqual(AuditMerkle.ComputeRoot(hashes), checkpoint.MerkleRoot))
        {
            throw new AuditExportException("The rows do not reproduce the checkpoint's Merkle root.");
        }

        var index = (int)(seq - checkpoint.FirstSeq);
        var file = new InclusionProofFile(
            FormatId,
            checkpoint.Id,
            seq,
            index,
            checkpoint.RowCount,
            hashes[index],
            AuditMerkle.BuildPath(hashes, index),
            checkpoint.MerkleRoot,
            new ProofCheckpoint(
                checkpoint.FirstSeq, checkpoint.LastSeq, checkpoint.RowCount, checkpoint.ChainHeadHash, checkpoint.MerkleRoot,
                checkpoint.CreatedAt.ToUniversalTime(), checkpoint.KeyId, checkpoint.Signature));

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file, AuditJsonIndented.Default.InclusionProofFile) + "\n");
        var check = AuditInclusionProofVerifier.Verify(bytes, verifier);
        if (!check.IsValid)
        {
            throw new AuditExportException("The proof did not verify: " + check.Reason);
        }

        return new BundleFile(PathFor(checkpoint, seq), bytes);
    }
}

/// <summary>
/// Pure offline verifier for proof files. No I/O and no instance. Any malformed or hostile input yields an invalid result.
/// </summary>
public static class AuditInclusionProofVerifier
{
    /// <summary>
    /// Verifies a proof file: the checkpoint id and range, the Merkle path from the leaf to the root, the root being the
    /// checkpoint's root, and the checkpoint signature. When <paramref name="anchoredRoot"/> is given (the root of the
    /// checkpoint as anchored externally, doc 48 section 5.3), the proof's root must equal it.
    /// </summary>
    public static AuditProofVerification Verify(ReadOnlySpan<byte> proofJson, IAuditSignatureVerifier verifier, byte[]? anchoredRoot = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);

        InclusionProofFile? file;
        try
        {
            file = JsonSerializer.Deserialize(proofJson, AuditJson.Default.InclusionProofFile);
        }
        catch (JsonException)
        {
            return AuditProofVerification.Invalid("The proof is not valid JSON.");
        }

        return Verify(file, verifier, anchoredRoot);
    }

    /// <summary>Verifies a parsed proof; see the byte overload.</summary>
    public static AuditProofVerification Verify(InclusionProofFile? file, IAuditSignatureVerifier verifier, byte[]? anchoredRoot = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);

        if (file is null || file.Checkpoint is null || file.LeafHash is null || file.Siblings is null || file.Root is null
            || file.CheckpointId is null || file.Checkpoint.ChainHeadHash is null || file.Checkpoint.MerkleRoot is null
            || file.Checkpoint.KeyId is null || file.Checkpoint.Signature is null || file.Siblings.Any(s => s is null))
        {
            return AuditProofVerification.Invalid("The proof is missing a required part.");
        }

        if (file.Format != AuditProofExporter.FormatId)
        {
            return AuditProofVerification.Invalid("Unknown proof format.");
        }

        var cp = file.Checkpoint;
        if (cp.ChainHeadHash.Length != AuditHashing.HashSize || cp.MerkleRoot.Length != AuditHashing.HashSize)
        {
            return AuditProofVerification.Invalid("The checkpoint has a hash of the wrong length.");
        }

        var record = new AuditCheckpointRecord(cp.FirstSeq, cp.LastSeq, cp.RowCount, cp.ChainHeadHash, cp.MerkleRoot, cp.CreatedAt, cp.KeyId, cp.Signature);
        if (cp.FirstSeq < 1 || cp.LastSeq < cp.FirstSeq || cp.LastSeq - cp.FirstSeq + 1 != cp.RowCount
            || !string.Equals(file.CheckpointId, record.Id, StringComparison.Ordinal))
        {
            return AuditProofVerification.Invalid("The checkpoint id or range is inconsistent.");
        }

        if (file.RowSeq < cp.FirstSeq || file.RowSeq > cp.LastSeq
            || file.LeafIndex != file.RowSeq - cp.FirstSeq || file.TreeSize != cp.RowCount)
        {
            return AuditProofVerification.Invalid("The row is not placed inside the checkpoint range.");
        }

        if (!AuditHashing.HashesEqual(file.Root, cp.MerkleRoot))
        {
            return AuditProofVerification.Invalid("The proof root is not the checkpoint's Merkle root.");
        }

        var recomputed = AuditMerkle.ComputeRootFromPath(file.LeafHash, file.LeafIndex, file.TreeSize, file.Siblings);
        if (recomputed is null || !AuditHashing.HashesEqual(recomputed, file.Root))
        {
            return AuditProofVerification.Invalid("The Merkle path does not lead to the root.");
        }

        if (!verifier.Verify(AuditHashing.CheckpointSignedBytes(record), cp.Signature))
        {
            return AuditProofVerification.Invalid("The checkpoint signature is not valid.");
        }

        if (anchoredRoot is not null && !AuditHashing.HashesEqual(anchoredRoot, file.Root))
        {
            return AuditProofVerification.Invalid("The proof root differs from the anchored root.");
        }

        return AuditProofVerification.Valid;
    }

    /// <summary>
    /// Also checks that <paramref name="row"/> is the row the proof is about: its fields hash to the leaf hash and its seq
    /// is the proof's row seq. Use it when the verifier holds the row itself, not only its hash.
    /// </summary>
    public static AuditProofVerification VerifyRow(
        ReadOnlySpan<byte> proofJson, AuditRow row, IAuditSignatureVerifier verifier, byte[]? anchoredRoot = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        var parsed = Verify(proofJson, verifier, anchoredRoot);
        if (!parsed.IsValid)
        {
            return parsed;
        }

        // The bytes parsed once already, so a second parse cannot fail.
        var file = JsonSerializer.Deserialize(proofJson, AuditJson.Default.InclusionProofFile)!;
        if (row.Seq != file.RowSeq || !AuditHashing.HashesEqual(AuditHashing.ComputeRowHash(row), file.LeafHash!))
        {
            return AuditProofVerification.Invalid("The row does not match the proof's leaf hash.");
        }

        return AuditProofVerification.Valid;
    }
}
