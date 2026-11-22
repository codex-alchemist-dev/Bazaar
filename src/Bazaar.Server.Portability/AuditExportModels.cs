using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Portability;

/// <summary>
/// One audit row as exported (doc 48 section 5.2). Mirrors the server's <c>AuditRecord</c>. <see cref="Payload"/> is the
/// stored text, so encrypted personal fields stay ciphertext; the row hash covers it as stored (doc 48 section 5.5).
/// </summary>
public sealed record AuditRow(
    long Seq,
    DateTimeOffset Ts,
    string Actor,
    string Kind,
    string Payload,
    byte[] PrevHash,
    byte[] Hash);

/// <summary>
/// A checkpoint exactly as stored, including its signature (doc 48 section 5.2). The exporter never re-signs one.
/// <see cref="Signature"/> is over <see cref="AuditHashing.CheckpointSignedBytes"/>.
/// </summary>
public sealed record AuditCheckpointRecord(
    long FirstSeq,
    long LastSeq,
    long RowCount,
    byte[] ChainHeadHash,
    byte[] MerkleRoot,
    DateTimeOffset CreatedAt,
    string KeyId,
    byte[] Signature)
{
    /// <summary>Stable id used in proof files and file names: <c>first-last</c>.</summary>
    public string Id => FirstSeq.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-"
        + LastSeq.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Narrow read side of the live audit chain for the exporter. The host implements it over the audit tables (its
/// <c>audit_log</c> and <c>audit_checkpoint</c> readers). It is read-only: the exporter never writes the live chain.
/// </summary>
public interface IAuditLogReadSource
{
    /// <summary>
    /// Checkpoints in order. With <paramref name="fromLastSeq"/> set, the first one returned is the checkpoint whose
    /// <c>LastSeq</c> equals it and the rest follow; with null, all of them from the first. An unknown start yields nothing.
    /// </summary>
    IAsyncEnumerable<AuditCheckpointRecord> ReadCheckpointsAsync(long? fromLastSeq, CancellationToken ct);

    /// <summary>The checkpoint whose range covers <paramref name="seq"/>, or null when the row is not yet checkpointed.</summary>
    Task<AuditCheckpointRecord?> ReadCheckpointCoveringAsync(long seq, CancellationToken ct);

    /// <summary>Rows in sequence order from <paramref name="fromSeq"/> through <paramref name="toSeq"/> (null: through the head).</summary>
    IAsyncEnumerable<AuditRow> ReadRowsAsync(long fromSeq, long? toSeq, CancellationToken ct);
}

/// <summary>
/// Verifies a checkpoint signature with the audit signing key's public half, so verification needs no network and no
/// instance. The algorithm belongs to the host's key; this contract only passes bytes through.
/// </summary>
public interface IAuditSignatureVerifier
{
    /// <summary>True when <paramref name="signature"/> is a valid signature over <paramref name="signedBytes"/>.</summary>
    bool Verify(ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> signature);
}

/// <summary>
/// Hashing rules mirrored from the server's audit chain (<c>AuditChain.ComputeHash</c>, <c>AuditCheckpoint.SignedBytes</c>)
/// so an export verifies offline. Pinned by test vectors produced from the server implementation.
/// </summary>
public static class AuditHashing
{
    /// <summary>SHA-256 output size; every hash and Merkle root has this length.</summary>
    public const int HashSize = SHA256.HashSizeInBytes;

    /// <summary>Format tag at the start of a checkpoint's signed bytes.</summary>
    public const string CheckpointDomain = "bazaar.audit.checkpoint.v1";

    /// <summary><c>prev_hash</c> of the first row of a chain: 32 zero bytes.</summary>
    public static byte[] GenesisHash() => new byte[HashSize];

    /// <summary>SHA-256 over big-endian seq, UTC ticks, then length-prefixed actor, kind, payload and prev hash.</summary>
    public static byte[] ComputeRowHash(long seq, DateTimeOffset ts, string actor, string kind, string payload, byte[] prevHash)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(prevHash);

        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(number, seq);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, ts.ToUniversalTime().UtcTicks);
        stream.Write(number);
        WriteField(stream, Encoding.UTF8.GetBytes(actor));
        WriteField(stream, Encoding.UTF8.GetBytes(kind));
        WriteField(stream, Encoding.UTF8.GetBytes(payload));
        WriteField(stream, prevHash);
        return SHA256.HashData(stream.ToArray());
    }

    /// <summary>Row hash recomputed from the row's own fields.</summary>
    public static byte[] ComputeRowHash(AuditRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return ComputeRowHash(row.Seq, row.Ts, row.Actor, row.Kind, row.Payload, row.PrevHash);
    }

    /// <summary>
    /// The bytes the audit key signs: length-prefixed domain tag, big-endian first seq, last seq and row count, the
    /// length-prefixed chain head and Merkle root, and the UTC ticks of the creation time.
    /// </summary>
    public static byte[] CheckpointSignedBytes(
        long firstSeq, long lastSeq, long rowCount, byte[] chainHeadHash, byte[] merkleRoot, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(chainHeadHash);
        ArgumentNullException.ThrowIfNull(merkleRoot);

        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[8];
        WriteField(stream, Encoding.UTF8.GetBytes(CheckpointDomain));
        BinaryPrimitives.WriteInt64BigEndian(number, firstSeq);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, lastSeq);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, rowCount);
        stream.Write(number);
        WriteField(stream, chainHeadHash);
        WriteField(stream, merkleRoot);
        BinaryPrimitives.WriteInt64BigEndian(number, createdAt.ToUniversalTime().UtcTicks);
        stream.Write(number);
        return stream.ToArray();
    }

    /// <summary>Signed bytes of a stored checkpoint.</summary>
    public static byte[] CheckpointSignedBytes(AuditCheckpointRecord checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return CheckpointSignedBytes(
            checkpoint.FirstSeq, checkpoint.LastSeq, checkpoint.RowCount, checkpoint.ChainHeadHash, checkpoint.MerkleRoot, checkpoint.CreatedAt);
    }

    /// <summary>Constant-time equality for hashes. Different lengths are unequal.</summary>
    public static bool HashesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    private static void WriteField(MemoryStream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}

/// <summary>
/// SHA-256 Merkle tree over row hashes, mirrored from the server's <c>MerkleTree</c>: leaf = SHA256(0x00 || rowHash),
/// node = SHA256(0x01 || left || right), a last odd node is promoted unchanged, an empty tree has root SHA256("").
/// </summary>
public static class AuditMerkle
{
    private const byte _leafPrefix = 0x00;
    private const byte _nodePrefix = 0x01;

    /// <summary>Merkle root over the row hashes, in row order.</summary>
    public static byte[] ComputeRoot(IReadOnlyList<byte[]> rowHashes)
    {
        ArgumentNullException.ThrowIfNull(rowHashes);
        if (rowHashes.Count == 0)
        {
            return SHA256.HashData(ReadOnlySpan<byte>.Empty);
        }

        var levels = BuildLevels(rowHashes);
        return levels[^1][0];
    }

    /// <summary>Sibling hashes from the leaf level up for the leaf at <paramref name="index"/>; promoted levels have no entry.</summary>
    public static IReadOnlyList<byte[]> BuildPath(IReadOnlyList<byte[]> rowHashes, int index)
    {
        ArgumentNullException.ThrowIfNull(rowHashes);
        if (index < 0 || index >= rowHashes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Leaf index is outside the tree.");
        }

        var levels = BuildLevels(rowHashes);
        var siblings = new List<byte[]>();
        var position = index;
        for (var depth = 0; depth < levels.Count - 1; depth++)
        {
            var level = levels[depth];
            if (position % 2 == 1)
            {
                siblings.Add((byte[])level[position - 1].Clone());
            }
            else if (position + 1 < level.Length)
            {
                siblings.Add((byte[])level[position + 1].Clone());
            }

            position /= 2;
        }

        return siblings;
    }

    /// <summary>
    /// Recomputes the root from a leaf hash, its index, the tree size and the sibling path. Returns null for malformed
    /// input (wrong hash length, index outside the tree, path too short or too long); never throws on bad data.
    /// </summary>
    public static byte[]? ComputeRootFromPath(ReadOnlySpan<byte> rowHash, long index, long treeSize, IReadOnlyList<byte[]> siblings)
    {
        if (rowHash.Length != AuditHashing.HashSize || treeSize < 1 || index < 0 || index >= treeSize)
        {
            return null;
        }

        var current = HashLeaf(rowHash);
        var position = index;
        var size = treeSize;
        var used = 0;
        while (size > 1)
        {
            if (position % 2 == 1 || position + 1 < size)
            {
                if (used >= siblings.Count || siblings[used] is not { Length: AuditHashing.HashSize } sibling)
                {
                    return null;
                }

                used++;
                current = position % 2 == 1 ? HashNode(sibling, current) : HashNode(current, sibling);
            }

            position /= 2;
            size = (size + 1) / 2;
        }

        return used == siblings.Count ? current : null;
    }

    private static List<byte[][]> BuildLevels(IReadOnlyList<byte[]> rowHashes)
    {
        var level = new byte[rowHashes.Count][];
        for (var i = 0; i < level.Length; i++)
        {
            if (rowHashes[i] is not { Length: AuditHashing.HashSize } hash)
            {
                throw new ArgumentException($"Row hash must be {AuditHashing.HashSize} bytes.", nameof(rowHashes));
            }

            level[i] = HashLeaf(hash);
        }

        var levels = new List<byte[][]> { level };
        while (level.Length > 1)
        {
            var next = new byte[(level.Length + 1) / 2][];
            for (var i = 0; i < next.Length; i++)
            {
                var left = 2 * i;
                next[i] = left + 1 < level.Length ? HashNode(level[left], level[left + 1]) : level[left];
            }

            levels.Add(next);
            level = next;
        }

        return levels;
    }

    private static byte[] HashLeaf(ReadOnlySpan<byte> rowHash)
    {
        Span<byte> input = stackalloc byte[1 + AuditHashing.HashSize];
        input[0] = _leafPrefix;
        rowHash.CopyTo(input[1..]);
        return SHA256.HashData(input);
    }

    private static byte[] HashNode(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Span<byte> input = stackalloc byte[1 + 2 * AuditHashing.HashSize];
        input[0] = _nodePrefix;
        left.CopyTo(input[1..]);
        right.CopyTo(input[(1 + AuditHashing.HashSize)..]);
        return SHA256.HashData(input);
    }
}
