namespace Bazaar.Kodo.Anchoring;

/// <summary>
/// Verifies Merkle proofs without the tree, following the iterative algorithms of RFC 9162 sections 2.1.3.2 and
/// 2.1.4.2. Every method returns false for malformed input (wrong lengths, out-of-range indexes, wrong proof length);
/// none of them throws on bad data.
/// </summary>
public static class MerkleVerifier
{
    /// <summary>Checks that <paramref name="recordHash"/> is leaf <see cref="MerkleInclusionProof.LeafIndex"/> of the tree with root <paramref name="root"/>.</summary>
    public static bool VerifyInclusion(ReadOnlySpan<byte> recordHash, MerkleInclusionProof proof, ReadOnlySpan<byte> root)
    {
        if (proof is null || recordHash.Length != MerkleHasher.HashLength || root.Length != MerkleHasher.HashLength || !AreHashes(proof.Path))
        {
            return false;
        }

        if (proof.TreeSize < 1 || proof.LeafIndex < 0 || proof.LeafIndex >= proof.TreeSize)
        {
            return false;
        }

        ulong index = (ulong)proof.LeafIndex;
        ulong last = (ulong)proof.TreeSize - 1;
        byte[] current = MerkleHasher.HashLeaf(recordHash);
        foreach (byte[] sibling in proof.Path)
        {
            if (last == 0)
            {
                return false;
            }

            if ((index & 1) == 1 || index == last)
            {
                current = MerkleHasher.HashNode(sibling, current);
                if ((index & 1) == 0)
                {
                    while ((index & 1) == 0 && index != 0)
                    {
                        index >>= 1;
                        last >>= 1;
                    }
                }
            }
            else
            {
                current = MerkleHasher.HashNode(current, sibling);
            }

            index >>= 1;
            last >>= 1;
        }

        return last == 0 && current.AsSpan().SequenceEqual(root);
    }

    /// <summary>Checks that the tree with root <paramref name="firstRoot"/> is a prefix of the tree with root <paramref name="secondRoot"/>.</summary>
    public static bool VerifyConsistency(MerkleConsistencyProof proof, ReadOnlySpan<byte> firstRoot, ReadOnlySpan<byte> secondRoot)
    {
        if (proof is null || firstRoot.Length != MerkleHasher.HashLength || secondRoot.Length != MerkleHasher.HashLength || !AreHashes(proof.Path))
        {
            return false;
        }

        if (proof.FirstSize < 1 || proof.FirstSize > proof.SecondSize)
        {
            return false;
        }

        if (proof.FirstSize == proof.SecondSize)
        {
            return proof.Path.Count == 0 && firstRoot.SequenceEqual(secondRoot);
        }

        List<byte[]> path = [];
        if ((proof.FirstSize & (proof.FirstSize - 1)) == 0)
        {
            path.Add(firstRoot.ToArray());
        }

        path.AddRange(proof.Path);
        if (path.Count == 0)
        {
            return false;
        }

        ulong first = (ulong)proof.FirstSize - 1;
        ulong second = (ulong)proof.SecondSize - 1;
        while ((first & 1) == 1)
        {
            first >>= 1;
            second >>= 1;
        }

        byte[] firstHash = path[0];
        byte[] secondHash = path[0];
        for (int i = 1; i < path.Count; i++)
        {
            if (second == 0)
            {
                return false;
            }

            byte[] step = path[i];
            if ((first & 1) == 1 || first == second)
            {
                firstHash = MerkleHasher.HashNode(step, firstHash);
                secondHash = MerkleHasher.HashNode(step, secondHash);
                if ((first & 1) == 0)
                {
                    while ((first & 1) == 0 && first != 0)
                    {
                        first >>= 1;
                        second >>= 1;
                    }
                }
            }
            else
            {
                secondHash = MerkleHasher.HashNode(secondHash, step);
            }

            first >>= 1;
            second >>= 1;
        }

        return second == 0 && firstHash.AsSpan().SequenceEqual(firstRoot) && secondHash.AsSpan().SequenceEqual(secondRoot);
    }

    private static bool AreHashes(IReadOnlyList<byte[]> path)
    {
        if (path is null)
        {
            return false;
        }

        foreach (byte[] hash in path)
        {
            if (hash is null || hash.Length != MerkleHasher.HashLength)
            {
                return false;
            }
        }

        return true;
    }
}
