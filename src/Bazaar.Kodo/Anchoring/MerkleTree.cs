namespace Bazaar.Kodo.Anchoring;

/// <summary>
/// Binary Merkle tree over journal record hashes, with the RFC 6962 shape: a tree of n leaves splits at the largest
/// power of two below n. A lone node is never duplicated or paired with itself (no CVE-2012-2459 style ambiguity),
/// and leaf and node hashing are domain separated (<see cref="MerkleHasher"/>). Pure logic: no I/O.
/// Each query recomputes the subtrees it needs, which is linear in the tree size; anchoring runs every
/// thousand records or quarter hour, so this stays cheap and keeps the type free of mutable state.
/// </summary>
public sealed class MerkleTree
{
    private readonly byte[][] _leaves;

    /// <summary>Builds the tree over record hashes (each 32 bytes), in journal order.</summary>
    public MerkleTree(IEnumerable<byte[]> recordHashes)
    {
        ArgumentNullException.ThrowIfNull(recordHashes);

        List<byte[]> leaves = [];
        foreach (byte[] hash in recordHashes)
        {
            ArgumentNullException.ThrowIfNull(hash);
            if (hash.Length != MerkleHasher.HashLength)
            {
                throw new ArgumentException($"Record hash must be {MerkleHasher.HashLength} bytes.", nameof(recordHashes));
            }

            leaves.Add(MerkleHasher.HashLeaf(hash));
        }

        _leaves = [.. leaves];
    }

    /// <summary>Number of leaves.</summary>
    public int Size => _leaves.Length;

    /// <summary>Root of the whole tree (the empty root when there are no leaves).</summary>
    public byte[] Root() => RootOfPrefix(Size);

    /// <summary>Root of the tree made of the first <paramref name="size"/> leaves.</summary>
    public byte[] RootOfPrefix(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, Size);
        return size == 0 ? MerkleHasher.EmptyRoot() : Subtree(0, size);
    }

    /// <summary>Inclusion proof for the leaf at <paramref name="index"/> in the tree of the first <paramref name="treeSize"/> leaves.</summary>
    public MerkleInclusionProof InclusionProof(int index, int treeSize)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(treeSize, Size);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, treeSize);

        List<byte[]> path = [];
        int start = 0;
        int end = treeSize;
        int position = index;
        while (end - start > 1)
        {
            int split = start + LargestPowerOfTwoBelow(end - start);
            if (position < split)
            {
                path.Add(Subtree(split, end));
                end = split;
            }
            else
            {
                path.Add(Subtree(start, split));
                start = split;
            }
        }

        path.Reverse();
        return new MerkleInclusionProof(index, treeSize, path);
    }

    /// <summary>Consistency proof from the tree of the first <paramref name="firstSize"/> leaves to the first <paramref name="secondSize"/>.</summary>
    public MerkleConsistencyProof ConsistencyProof(int firstSize, int secondSize)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(secondSize, Size);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstSize, secondSize);

        List<byte[]> path = [];
        if (firstSize < secondSize)
        {
            int start = 0;
            int end = secondSize;
            int m = firstSize;
            bool complete = true;
            while (m != end - start)
            {
                int split = start + LargestPowerOfTwoBelow(end - start);
                if (m <= split - start)
                {
                    path.Add(Subtree(split, end));
                    end = split;
                }
                else
                {
                    path.Add(Subtree(start, split));
                    m -= split - start;
                    start = split;
                    complete = false;
                }
            }

            if (!complete)
            {
                path.Add(Subtree(start, end));
            }

            path.Reverse();
        }

        return new MerkleConsistencyProof(firstSize, secondSize, path);
    }

    /// <summary>Largest power of two strictly below <paramref name="n"/> (n at least 2).</summary>
    internal static int LargestPowerOfTwoBelow(int n) => 1 << (31 - int.LeadingZeroCount(n - 1));

    private byte[] Subtree(int start, int end)
    {
        if (end - start == 1)
        {
            return _leaves[start];
        }

        int split = start + LargestPowerOfTwoBelow(end - start);
        return MerkleHasher.HashNode(Subtree(start, split), Subtree(split, end));
    }
}
