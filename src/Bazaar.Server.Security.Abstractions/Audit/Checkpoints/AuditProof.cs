using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Security.Audit.Checkpoints;

/// <summary>
/// Exportable Merkle inclusion proof for one audit row against a signed checkpoint (design doc 48, section 5.3).
/// Byte fields are Base64 in JSON. The document is self-contained, so it can be verified offline with a public key.
/// </summary>
public sealed record AuditProofDocument(
    string Format,
    AuditProofRow Row,
    IReadOnlyList<byte[]> Siblings,
    AuditProofCheckpoint Checkpoint,
    byte[] Signature);

/// <summary>The audit row being proved (the fields that <see cref="AuditChain.ComputeHash"/> covers, plus the hash).</summary>
public sealed record AuditProofRow(
    long Seq,
    DateTimeOffset Ts,
    string Actor,
    string Kind,
    string Payload,
    byte[] PrevHash,
    byte[] Hash);

/// <summary>The checkpoint fields, as stored in <c>audit_checkpoint</c>, plus the id of the key that signed it.</summary>
public sealed record AuditProofCheckpoint(
    long FirstSeq,
    long LastSeq,
    long RowCount,
    byte[] ChainHeadHash,
    byte[] MerkleRoot,
    DateTimeOffset CreatedAt,
    string KeyId);

/// <summary>Source-generated JSON for the proof file (no reflection serializer).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AuditProofDocument))]
internal sealed partial class AuditProofJsonContext : JsonSerializerContext;

/// <summary>Reads and writes the proof file.</summary>
public static class AuditProofFile
{
    /// <summary>Format id stored in <see cref="AuditProofDocument.Format"/>.</summary>
    public const string FormatId = "bazaar.audit.proof.v1";

    /// <summary>Serializes the proof to JSON.</summary>
    public static string Write(AuditProofDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, AuditProofJsonContext.Default.AuditProofDocument);
    }

    /// <summary>Parses a proof file. Returns null for malformed JSON or a missing required part; never throws on bad input.</summary>
    public static AuditProofDocument? TryRead(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, AuditProofJsonContext.Default.AuditProofDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Builds inclusion proofs for rows covered by a checkpoint, reusing <see cref="MerkleTree"/> as is.</summary>
public static class AuditProofBuilder
{
    /// <summary>
    /// Builds the proof for <paramref name="seq"/>. <paramref name="records"/> must be exactly the rows of the
    /// checkpoint range, in order, and must reproduce the checkpoint's Merkle root.
    /// </summary>
    /// <exception cref="ArgumentException">The seq is outside the range, or the rows do not match the checkpoint.</exception>
    public static AuditProofDocument Build(
        AuditCheckpoint checkpoint, string keyId, byte[] signature, IReadOnlyList<AuditRecord> records, long seq)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(records);

        if (seq < checkpoint.FirstSeq || seq > checkpoint.LastSeq)
        {
            throw new ArgumentException($"Seq {seq} is outside the checkpoint range {checkpoint.FirstSeq}..{checkpoint.LastSeq}.", nameof(seq));
        }

        var count = checkpoint.LastSeq - checkpoint.FirstSeq + 1;
        if (records.Count != count || checkpoint.RowCount != count)
        {
            throw new ArgumentException("Records do not match the checkpoint range.", nameof(records));
        }

        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].Seq != checkpoint.FirstSeq + i)
            {
                throw new ArgumentException("Records are not the contiguous checkpoint range.", nameof(records));
            }
        }

        var tree = new MerkleTree(records.Select(r => r.Hash));
        if (!tree.Root.AsSpan().SequenceEqual(checkpoint.MerkleRoot))
        {
            throw new ArgumentException("Records do not reproduce the checkpoint Merkle root.", nameof(records));
        }

        var index = (int)(seq - checkpoint.FirstSeq);
        var row = records[index];
        var path = tree.GetInclusionProof(index);

        return new AuditProofDocument(
            AuditProofFile.FormatId,
            new AuditProofRow(row.Seq, row.Ts, row.Actor, row.Kind, row.Payload, row.PrevHash, row.Hash),
            path.Siblings,
            new AuditProofCheckpoint(
                checkpoint.FirstSeq, checkpoint.LastSeq, checkpoint.RowCount, checkpoint.ChainHeadHash,
                checkpoint.MerkleRoot, checkpoint.CreatedAt, keyId),
            signature);
    }
}

/// <summary>Result of an audit proof verification.</summary>
public sealed record AuditProofResult(bool IsValid, string Reason)
{
    /// <summary>The proof verified.</summary>
    public static AuditProofResult Valid { get; } = new(true, "ok");

    /// <summary>The proof failed, with the reason.</summary>
    public static AuditProofResult Invalid(string reason) => new(false, reason);
}

/// <summary>Offline verifier for proof files. Hostile input yields Invalid, never an exception.</summary>
public static class AuditProofVerifier
{
    /// <summary>Parses and verifies a proof file.</summary>
    public static AuditProofResult Verify(string? json, IAuditCheckpointSigner signer) =>
        Verify(AuditProofFile.TryRead(json), signer);

    /// <summary>Verifies a parsed proof: row hash, Merkle path to the checkpoint root, range, and signature.</summary>
    public static AuditProofResult Verify(AuditProofDocument? document, IAuditCheckpointSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        try
        {
            return VerifyCore(document, signer);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException
            or FormatException or NullReferenceException or CryptographicException)
        {
            return AuditProofResult.Invalid("Proof is malformed.");
        }
    }

    private static AuditProofResult VerifyCore(AuditProofDocument? document, IAuditCheckpointSigner signer)
    {
        if (document is null || document.Row is null || document.Checkpoint is null
            || document.Siblings is null || document.Signature is null)
        {
            return AuditProofResult.Invalid("Proof is missing a required part.");
        }

        if (document.Format != AuditProofFile.FormatId)
        {
            return AuditProofResult.Invalid("Unknown proof format.");
        }

        var row = document.Row;
        var cp = document.Checkpoint;
        if (row.Actor is null || row.Kind is null || row.Payload is null || row.PrevHash is null || row.Hash is null
            || cp.ChainHeadHash is null || cp.MerkleRoot is null || document.Siblings.Any(s => s is null))
        {
            return AuditProofResult.Invalid("Proof is missing a required part.");
        }

        if (cp.FirstSeq < 1 || cp.LastSeq < cp.FirstSeq || cp.LastSeq - cp.FirstSeq + 1 != cp.RowCount)
        {
            return AuditProofResult.Invalid("Checkpoint row_count does not match its range.");
        }

        if (row.Seq < cp.FirstSeq || row.Seq > cp.LastSeq)
        {
            return AuditProofResult.Invalid("Row seq is outside the checkpoint range.");
        }

        var recomputed = AuditChain.ComputeHash(row.Seq, row.Ts, row.Actor, row.Kind, row.Payload, row.PrevHash);
        if (!recomputed.AsSpan().SequenceEqual(row.Hash))
        {
            return AuditProofResult.Invalid("Row hash does not match its fields.");
        }

        if (cp.RowCount > int.MaxValue)
        {
            return AuditProofResult.Invalid("Checkpoint range is too large.");
        }

        var path = new MerkleInclusionProof((int)cp.RowCount, document.Siblings);
        if (!MerkleTree.Verify(row.Hash, row.Seq - cp.FirstSeq, path, cp.MerkleRoot))
        {
            return AuditProofResult.Invalid("Merkle path does not lead to the checkpoint root.");
        }

        var checkpoint = new AuditCheckpoint(cp.FirstSeq, cp.LastSeq, cp.RowCount, cp.ChainHeadHash, cp.MerkleRoot, cp.CreatedAt);
        if (!signer.Verify(checkpoint.SignedBytes(), document.Signature))
        {
            return AuditProofResult.Invalid("Checkpoint signature is not valid.");
        }

        return AuditProofResult.Valid;
    }
}
