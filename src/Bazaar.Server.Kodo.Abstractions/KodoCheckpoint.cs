using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Anchoring;
using Bazaar.Kodo.Journal;

namespace Bazaar.Server.Kodo;

/// <summary>
/// A checkpoint of one window of journal records (doc 46 section 6.5.2, PROPOSED): the window's first and last sequence, its time range,
/// the head hash, the hash the window links to, and a Merkle root over the window's record hashes. <see cref="Digest"/> binds all of it.
/// <see cref="Signature"/> is set only when a signer is registered; the journal key lives in the Identity Service, which is not built.
/// </summary>
public sealed record KodoCheckpoint
{
    /// <summary>Sequence of the first record in the window.</summary>
    public required long FirstSequence { get; init; }

    /// <summary>Sequence of the last record in the window, which is the journal head at the cut.</summary>
    public required long LastSequence { get; init; }

    /// <summary>When the first record was written, in UTC.</summary>
    public required DateTimeOffset FirstAt { get; init; }

    /// <summary>When the last record was written, in UTC.</summary>
    public required DateTimeOffset LastAt { get; init; }

    /// <summary>When the checkpoint was cut, in UTC.</summary>
    public required DateTimeOffset CutAt { get; init; }

    /// <summary>Hash of the record before the window (the previous checkpoint's head, or the first record's link for the first window).</summary>
    public required byte[] PreviousHead { get; init; }

    /// <summary>Hash of the last record in the window.</summary>
    public required byte[] HeadHash { get; init; }

    /// <summary>Merkle root over the record hashes of the window (the tree of 6.5.1).</summary>
    public required byte[] Root { get; init; }

    /// <summary>SHA-256 over the canonical fields, see <see cref="KodoCheckpoints.ComputeDigest"/>.</summary>
    public required byte[] Digest { get; init; }

    /// <summary>Signature over <see cref="Digest"/>, or null when unsigned.</summary>
    public byte[]? Signature { get; init; }

    /// <summary>Identifier of the key that signed, or null when unsigned.</summary>
    public string? SignerId { get; init; }

    /// <summary>Number of records in the window.</summary>
    public long RecordCount => LastSequence - FirstSequence + 1;
}

/// <summary>Signs checkpoint digests. Interface only: no key provider exists yet (doc 46 section 6.5.2). Without one, checkpoints are digest-bound and unsigned.</summary>
public interface IKodoCheckpointSigner
{
    /// <summary>Identifier of the signing key, 1 to 128 characters.</summary>
    string Id { get; }

    /// <summary>Signs the 32-byte digest. May throw; the checkpoint is then stored unsigned.</summary>
    ValueTask<byte[]> SignAsync(byte[] digest, CancellationToken cancellationToken);
}

/// <summary>Pure checkpoint construction and checks.</summary>
public static class KodoCheckpoints
{
    private static readonly byte[] _domain = "bazaar.kodo.checkpoint.v1"u8.ToArray();

    /// <summary>
    /// Builds an unsigned checkpoint over <paramref name="window"/>: consecutive, linked records. Null when the window is empty, has
    /// a gap, or a record does not link to the one before it.
    /// </summary>
    public static KodoCheckpoint? Build(IReadOnlyList<JournalRecord> window, DateTimeOffset cutAt)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Count == 0)
        {
            return null;
        }

        for (int i = 1; i < window.Count; i++)
        {
            if (window[i].Sequence != window[i - 1].Sequence + 1 || !window[i].PreviousHash.AsSpan().SequenceEqual(window[i - 1].Hash))
            {
                return null;
            }
        }

        JournalRecord first = window[0], last = window[^1];
        KodoCheckpoint unsigned = new()
        {
            FirstSequence = first.Sequence,
            LastSequence = last.Sequence,
            FirstAt = first.At.ToUniversalTime(),
            LastAt = last.At.ToUniversalTime(),
            CutAt = cutAt.ToUniversalTime(),
            PreviousHead = [.. first.PreviousHash],
            HeadHash = [.. last.Hash],
            Root = new MerkleTree(window.Select(r => r.Hash)).Root(),
            Digest = [],
        };
        return unsigned with { Digest = ComputeDigest(unsigned) };
    }

    /// <summary>SHA-256 over a domain tag, the three sequence and time fields, and the three hashes. The signature fields are not covered.</summary>
    public static byte[] ComputeDigest(KodoCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(_domain);
        Span<byte> number = stackalloc byte[8];
        foreach (long value in new[] { checkpoint.FirstSequence, checkpoint.LastSequence, checkpoint.FirstAt.UtcTicks, checkpoint.LastAt.UtcTicks, checkpoint.CutAt.UtcTicks })
        {
            BinaryPrimitives.WriteInt64BigEndian(number, value);
            hash.AppendData(number);
        }

        hash.AppendData(checkpoint.PreviousHead);
        hash.AppendData(checkpoint.HeadHash);
        hash.AppendData(checkpoint.Root);
        return hash.GetHashAndReset();
    }

    /// <summary>True when the fields are in range and the stored digest matches them.</summary>
    public static bool DigestIsValid(KodoCheckpoint checkpoint) =>
        checkpoint is { FirstSequence: >= 0 } && checkpoint.LastSequence >= checkpoint.FirstSequence
        && checkpoint.PreviousHead is { Length: JournalChain.HashLength } && checkpoint.HeadHash is { Length: JournalChain.HashLength }
        && checkpoint.Root is { Length: JournalChain.HashLength } && checkpoint.Digest is { Length: JournalChain.HashLength }
        && checkpoint.Digest.AsSpan().SequenceEqual(ComputeDigest(checkpoint));

    /// <summary>The anchor of a checkpoint for one sink: the window's record count, root and head hash (doc 46 section 6.5.2).</summary>
    public static AnchorRecord ToAnchor(KodoCheckpoint checkpoint, string sinkId, DateTimeOffset anchoredAt)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        AnchorRecord anchor = new()
        {
            Version = AnchorRecord.CurrentVersion,
            SinkId = sinkId,
            TreeSize = checkpoint.RecordCount,
            Root = [.. checkpoint.Root],
            JournalHead = [.. checkpoint.HeadHash],
            AnchoredAt = anchoredAt.ToUniversalTime(),
        };
        return anchor.IsWellFormed ? anchor : throw new ArgumentException("The checkpoint or sink id does not make a well-formed anchor.");
    }

    /// <summary>
    /// The proof of the record at <paramref name="sequence"/> against <paramref name="checkpoint"/>, from the stored hashes of its window.
    /// <paramref name="windowHashes"/> holds the (sequence, hash) pairs in sequence order. A short or holed window is
    /// <see cref="KodoCheckpointProofOutcome.Pruned"/>; a bad digest or a root that differs is <see cref="KodoCheckpointProofOutcome.Inconsistent"/>.
    /// </summary>
    public static KodoCheckpointProof BuildProof(long sequence, KodoCheckpoint checkpoint, IReadOnlyList<KeyValuePair<long, byte[]>> windowHashes)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(windowHashes);
        if (!DigestIsValid(checkpoint))
        {
            return new KodoCheckpointProof(KodoCheckpointProofOutcome.Inconsistent);
        }

        if (sequence < checkpoint.FirstSequence || sequence > checkpoint.LastSequence)
        {
            return new KodoCheckpointProof(KodoCheckpointProofOutcome.NotCheckpointed);
        }

        if (windowHashes.Count != checkpoint.RecordCount)
        {
            return new KodoCheckpointProof(KodoCheckpointProofOutcome.Pruned);
        }

        for (int i = 0; i < windowHashes.Count; i++)
        {
            if (windowHashes[i].Key != checkpoint.FirstSequence + i)
            {
                return new KodoCheckpointProof(KodoCheckpointProofOutcome.Pruned);
            }
        }

        MerkleTree tree = new(windowHashes.Select(p => p.Value));
        if (!tree.Root().AsSpan().SequenceEqual(checkpoint.Root))
        {
            return new KodoCheckpointProof(KodoCheckpointProofOutcome.Inconsistent);
        }

        int index = checked((int)(sequence - checkpoint.FirstSequence));
        MerkleInclusionProof proof = tree.InclusionProof(index, tree.Size);
        return new KodoCheckpointProof(
            KodoCheckpointProofOutcome.Found,
            new KodoJournalProofView(
                sequence,
                proof.TreeSize,
                Convert.ToHexStringLower(windowHashes[index].Value),
                Convert.ToHexStringLower(tree.Root()),
                proof.Path.Select(Convert.ToHexStringLower).ToArray(),
                checkpoint.FirstSequence,
                checkpoint.LastSequence));
    }
}

/// <summary>The retention rule that keeps proofs and anchors usable (doc 46 section 6.5.2).</summary>
public static class KodoPruneRule
{
    /// <summary>
    /// The highest sequence retention may remove: never the head, and never a record after the newest anchored checkpoint, so the
    /// records between that checkpoint and the head stay until a newer checkpoint is anchored. Returns -1 (remove nothing) while no
    /// checkpoint is anchored.
    /// </summary>
    public static long HighestPrunable(long headSequence, long? newestAnchoredSequence) =>
        newestAnchoredSequence is not { } anchored ? -1 : Math.Min(anchored, headSequence - 1);
}
