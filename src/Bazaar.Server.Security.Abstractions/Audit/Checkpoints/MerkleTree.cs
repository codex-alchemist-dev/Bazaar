using System.Security.Cryptography;

namespace Bazaar.Server.Security.Audit.Checkpoints;

/// <summary>
/// SHA-256 Merkle tree over audit row hashes (<see cref="AuditRecord.Hash"/>). Pure logic: no I/O and no storage.
/// Used by checkpoints (design doc 48, section 5.3) to prove that a row belongs to an anchored checkpoint.
/// </summary>
/// <remarks>
/// Encoding, with domain separation so a leaf can never be read as an inner node:
/// <list type="bullet">
/// <item>leaf = SHA256(0x00 || leafHash)</item>
/// <item>node = SHA256(0x01 || left || right)</item>
/// <item>
/// A node left without a sibling at its level (the last node of an odd-length level) is promoted unchanged to the next
/// level. It is not paired with itself and not duplicated.
/// </item>
/// <item>
/// The empty tree has root SHA256 of the empty input. No leaf or node hash can equal it, because those hashes are
/// always computed over at least 33 bytes with a prefix.
/// </item>
/// </list>
/// A single leaf is its own root (its leaf hash, with the 0x00 prefix applied).
/// </remarks>
public sealed class MerkleTree
{
    /// <summary>Byte length of every leaf and root hash (SHA-256).</summary>
    public const int HashSize = SHA256.HashSizeInBytes;

    private const byte _leafPrefix = 0x00;
    private const byte _nodePrefix = 0x01;

    private readonly List<byte[]> _leafHashes;
    private readonly List<byte[][]> _levels;

    /// <summary>Builds the tree over the given audit row hashes, in row order. Each hash must be 32 bytes.</summary>
    public MerkleTree(IEnumerable<byte[]> leafHashes)
    {
        ArgumentNullException.ThrowIfNull(leafHashes);

        _leafHashes = [];
        foreach (var leafHash in leafHashes)
        {
            ArgumentNullException.ThrowIfNull(leafHash);
            if (leafHash.Length != HashSize)
            {
                throw new ArgumentException($"Leaf hash must be {HashSize} bytes.", nameof(leafHashes));
            }

            _leafHashes.Add((byte[])leafHash.Clone());
        }

        _levels = [];
        if (_leafHashes.Count == 0)
        {
            Root = SHA256.HashData(ReadOnlySpan<byte>.Empty);
            return;
        }

        var level = new byte[_leafHashes.Count][];
        for (var i = 0; i < level.Length; i++)
        {
            level[i] = HashLeaf(_leafHashes[i]);
        }

        _levels.Add(level);
        while (level.Length > 1)
        {
            var next = new byte[(level.Length + 1) / 2][];
            for (var i = 0; i < next.Length; i++)
            {
                var left = 2 * i;
                next[i] = left + 1 < level.Length ? HashNode(level[left], level[left + 1]) : level[left];
            }

            _levels.Add(next);
            level = next;
        }

        Root = level[0];
    }

    /// <summary>Number of leaves (audit rows) in the tree.</summary>
    public int LeafCount => _leafHashes.Count;

    /// <summary>Merkle root. For an empty tree this is SHA256 of the empty input (see the remarks).</summary>
    public byte[] Root { get; }

    /// <summary>Builds the inclusion proof for the leaf at <paramref name="index"/> (0-based, in row order).</summary>
    public MerkleInclusionProof GetInclusionProof(int index)
    {
        if (index < 0 || index >= LeafCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Leaf index is outside the tree.");
        }

        var siblings = new List<byte[]>();
        var position = index;
        // The last level holds only the root and has no sibling, so it is not walked.
        for (var depth = 0; depth < _levels.Count - 1; depth++)
        {
            var level = _levels[depth];
            if (position % 2 == 1)
            {
                siblings.Add((byte[])level[position - 1].Clone());
            }
            else if (position + 1 < level.Length)
            {
                siblings.Add((byte[])level[position + 1].Clone());
            }

            // An even position with no right neighbour is promoted, so the proof has no entry for this level.
            position /= 2;
        }

        return new MerkleInclusionProof(LeafCount, siblings);
    }

    /// <summary>
    /// Checks that <paramref name="leafHash"/> is the leaf at <paramref name="index"/> of a tree with the root
    /// <paramref name="expectedRoot"/>. The proof carries the tree size, so a verifier needs nothing else.
    /// Returns false for any malformed input; it never throws on bad data.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> leafHash, long index, MerkleInclusionProof proof, ReadOnlySpan<byte> expectedRoot)
    {
        ArgumentNullException.ThrowIfNull(proof);

        if (leafHash.Length != HashSize || expectedRoot.Length != HashSize)
        {
            return false;
        }

        if (proof.TreeSize < 1 || index < 0 || index >= proof.TreeSize)
        {
            return false;
        }

        var current = HashLeaf(leafHash);
        var position = index;
        var size = proof.TreeSize;
        var used = 0;
        while (size > 1)
        {
            if (position % 2 == 1)
            {
                if (used >= proof.Siblings.Count)
                {
                    return false;
                }

                current = HashNode(proof.Siblings[used++], current);
            }
            else if (position + 1 < size)
            {
                if (used >= proof.Siblings.Count)
                {
                    return false;
                }

                current = HashNode(current, proof.Siblings[used++]);
            }

            // Otherwise the node is promoted unchanged, exactly as the tree builder does.
            position /= 2;
            size = (size + 1) / 2;
        }

        return used == proof.Siblings.Count && current.AsSpan().SequenceEqual(expectedRoot);
    }

    private static byte[] HashLeaf(ReadOnlySpan<byte> leafHash)
    {
        Span<byte> input = stackalloc byte[1 + HashSize];
        input[0] = _leafPrefix;
        leafHash.CopyTo(input[1..]);
        return SHA256.HashData(input);
    }

    private static byte[] HashNode(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Span<byte> input = stackalloc byte[1 + 2 * HashSize];
        input[0] = _nodePrefix;
        left.CopyTo(input[1..]);
        right.CopyTo(input[(1 + HashSize)..]);
        return SHA256.HashData(input);
    }
}

/// <summary>
/// Inclusion proof for one leaf: the tree size it was built against, and the sibling hashes from the leaf level up.
/// Levels where the node was promoted have no entry, so the size is needed to read the proof.
/// </summary>
public sealed record MerkleInclusionProof(int TreeSize, IReadOnlyList<byte[]> Siblings);
