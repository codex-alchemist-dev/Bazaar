using Bazaar.Kodo.Anchoring;

namespace Bazaar.Kodo.Tests.Anchoring;

public sealed class MerkleTreeTests
{
    internal static List<byte[]> Hashes(int count) =>
        [.. Enumerable.Range(1, count).Select(i => Enumerable.Repeat((byte)i, 32).ToArray())];

    [Fact]
    public void EmptyRootIsSha256OfNothing()
    {
        Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", Convert.ToHexString(new MerkleTree([]).Root()));
    }

    [Fact]
    public void KnownAnswerVectors()
    {
        Assert.Equal("7F9C9E31AC8256CA2F258583DF262DBC7D6F68F2A03043D5C99A4AE5A7396CE9", Convert.ToHexString(MerkleHasher.HashLeaf(new byte[32])));
        Assert.Equal("DCFFE786DED16D283C663846AD0C4FF26558FCCDE36CA9D30B2EA19EADE9FC0E", Convert.ToHexString(new MerkleTree(Hashes(1)).Root()));
        Assert.Equal("3A066E0F40C6A1981EBFA60D2411625D0517AE22C2FC8C7C1784FF8A75C78565", Convert.ToHexString(new MerkleTree(Hashes(2)).Root()));
        Assert.Equal("DF896896C799531F1FD1E556CEA26A6989AB06853BCBFDD3E4F5097A611F658F", Convert.ToHexString(new MerkleTree(Hashes(3)).Root()));
    }

    [Fact]
    public void LastLeafIsNeverDuplicated()
    {
        // Under the duplicate-last-leaf rule, trees [a,b,c] and [a,b,c,c] share a root. Here they must not.
        List<byte[]> three = Hashes(3);
        List<byte[]> four = [.. three, three[2]];

        Assert.NotEqual(new MerkleTree(three).Root(), new MerkleTree(four).Root());
    }

    [Fact]
    public void RootOfPrefixMatchesTreeBuiltOverThatPrefix()
    {
        MerkleTree full = new(Hashes(40));
        for (int size = 0; size <= 40; size++)
        {
            Assert.Equal(new MerkleTree(Hashes(size)).Root(), full.RootOfPrefix(size));
        }
    }

    [Fact]
    public void RejectsBadLeafLength()
    {
        Assert.Throws<ArgumentException>(() => new MerkleTree([new byte[31]]));
    }

    [Fact]
    public void InclusionProofsVerifyForEveryIndexOfEverySizeUpTo70()
    {
        for (int size = 1; size <= 70; size++)
        {
            List<byte[]> hashes = Hashes(size);
            MerkleTree tree = new(hashes);
            byte[] root = tree.Root();
            for (int index = 0; index < size; index++)
            {
                MerkleInclusionProof proof = tree.InclusionProof(index, size);
                Assert.True(MerkleVerifier.VerifyInclusion(hashes[index], proof, root), $"size {size} index {index}");
                Assert.True(proof.Path.Count <= 7);
            }
        }
    }

    [Fact]
    public void InclusionProofAgainstEarlierPrefixVerifiesAgainstThatPrefixRoot()
    {
        List<byte[]> hashes = Hashes(50);
        MerkleTree tree = new(hashes);
        MerkleInclusionProof proof = tree.InclusionProof(9, 33);

        Assert.True(MerkleVerifier.VerifyInclusion(hashes[9], proof, tree.RootOfPrefix(33)));
        Assert.False(MerkleVerifier.VerifyInclusion(hashes[9], proof, tree.Root()));
    }

    [Fact]
    public void ConsistencyProofsVerifyForAllSizePairsUpTo40()
    {
        MerkleTree tree = new(Hashes(40));
        for (int second = 1; second <= 40; second++)
        {
            for (int first = 1; first <= second; first++)
            {
                MerkleConsistencyProof proof = tree.ConsistencyProof(first, second);
                Assert.True(
                    MerkleVerifier.VerifyConsistency(proof, tree.RootOfPrefix(first), tree.RootOfPrefix(second)),
                    $"{first} -> {second}");
            }
        }
    }

    [Fact]
    public void InclusionTamperingIsRejected()
    {
        List<byte[]> hashes = Hashes(13);
        MerkleTree tree = new(hashes);
        byte[] root = tree.Root();

        for (int index = 0; index < 13; index++)
        {
            MerkleInclusionProof proof = tree.InclusionProof(index, 13);

            // Flip any byte of any path hash, the leaf, or the root.
            for (int p = 0; p < proof.Path.Count; p++)
            {
                for (int b = 0; b < 32; b++)
                {
                    List<byte[]> path = [.. proof.Path];
                    path[p] = Flip(path[p], b);
                    Assert.False(MerkleVerifier.VerifyInclusion(hashes[index], proof with { Path = path }, root));
                }
            }

            for (int b = 0; b < 32; b++)
            {
                Assert.False(MerkleVerifier.VerifyInclusion(Flip(hashes[index], b), proof, root));
                Assert.False(MerkleVerifier.VerifyInclusion(hashes[index], proof, Flip(root, b)));
            }

            // Truncated, extended, wrong index, wrong size.
            Assert.False(MerkleVerifier.VerifyInclusion(hashes[index], proof with { Path = [.. proof.Path.Take(proof.Path.Count - 1)] }, root));
            Assert.False(MerkleVerifier.VerifyInclusion(hashes[index], proof with { Path = [.. proof.Path, new byte[32]] }, root));
            Assert.False(MerkleVerifier.VerifyInclusion(hashes[index], proof with { LeafIndex = (index + 1) % 13 }, root));
            Assert.False(MerkleVerifier.VerifyInclusion(hashes[index], proof with { TreeSize = 64 }, root));
            Assert.False(MerkleVerifier.VerifyInclusion(hashes[index], proof with { LeafIndex = -1 }, root));
            Assert.False(MerkleVerifier.VerifyInclusion(hashes[index], proof with { LeafIndex = 13 }, root));
            Assert.False(MerkleVerifier.VerifyInclusion(hashes[index], proof with { Path = [.. proof.Path.Select(h => h[..31])] }, root));
        }
    }

    [Fact]
    public void SwappedSiblingsAreRejected()
    {
        List<byte[]> hashes = Hashes(16);
        MerkleTree tree = new(hashes);
        MerkleInclusionProof proof = tree.InclusionProof(5, 16);
        List<byte[]> swapped = [.. proof.Path];
        (swapped[0], swapped[1]) = (swapped[1], swapped[0]);

        Assert.False(MerkleVerifier.VerifyInclusion(hashes[5], proof with { Path = swapped }, tree.Root()));
    }

    [Fact]
    public void InnerNodeCannotPassAsLeaf()
    {
        // Second-preimage attempt: present the hash of an inner node as if it were a record hash in a smaller tree.
        List<byte[]> hashes = Hashes(4);
        MerkleTree tree = new(hashes);
        byte[] left = MerkleHasher.HashNode(MerkleHasher.HashLeaf(hashes[0]), MerkleHasher.HashLeaf(hashes[1]));
        byte[] right = MerkleHasher.HashNode(MerkleHasher.HashLeaf(hashes[2]), MerkleHasher.HashLeaf(hashes[3]));
        MerkleInclusionProof forged = new(0, 2, [right]);

        Assert.True(MerkleVerifier.VerifyInclusion(hashes[0], tree.InclusionProof(0, 4), tree.Root()));
        Assert.False(MerkleVerifier.VerifyInclusion(left, forged, tree.Root()));
        // The concatenated children as a 64-byte leaf is also not a valid 32-byte record hash.
        Assert.NotEqual(tree.Root(), MerkleHasher.HashLeaf([.. left, .. right]));
    }

    [Fact]
    public void ConsistencyTamperingIsRejected()
    {
        MerkleTree tree = new(Hashes(21));
        foreach ((int first, int second) in new[] { (1, 21), (5, 21), (8, 21), (13, 20), (16, 21), (20, 21) })
        {
            MerkleConsistencyProof proof = tree.ConsistencyProof(first, second);
            byte[] firstRoot = tree.RootOfPrefix(first);
            byte[] secondRoot = tree.RootOfPrefix(second);

            for (int p = 0; p < proof.Path.Count; p++)
            {
                for (int b = 0; b < 32; b++)
                {
                    List<byte[]> path = [.. proof.Path];
                    path[p] = Flip(path[p], b);
                    Assert.False(MerkleVerifier.VerifyConsistency(proof with { Path = path }, firstRoot, secondRoot));
                }
            }

            for (int b = 0; b < 32; b++)
            {
                Assert.False(MerkleVerifier.VerifyConsistency(proof, Flip(firstRoot, b), secondRoot));
                Assert.False(MerkleVerifier.VerifyConsistency(proof, firstRoot, Flip(secondRoot, b)));
            }

            Assert.False(MerkleVerifier.VerifyConsistency(proof with { Path = [.. proof.Path.Take(proof.Path.Count - 1)] }, firstRoot, secondRoot));
            Assert.False(MerkleVerifier.VerifyConsistency(proof with { Path = [.. proof.Path, new byte[32]] }, firstRoot, secondRoot));
            Assert.False(MerkleVerifier.VerifyConsistency(proof with { FirstSize = 0 }, firstRoot, secondRoot));
            Assert.False(MerkleVerifier.VerifyConsistency(proof with { FirstSize = second + 1 }, firstRoot, secondRoot));
        }
    }

    [Fact]
    public void ForkedHistoryFailsConsistency()
    {
        List<byte[]> honest = Hashes(30);
        List<byte[]> forked = [.. honest];
        forked[3] = new byte[32];
        MerkleTree honestTree = new(honest);
        MerkleTree forkedTree = new(forked);

        MerkleConsistencyProof proof = forkedTree.ConsistencyProof(10, 30);

        Assert.True(MerkleVerifier.VerifyConsistency(proof, forkedTree.RootOfPrefix(10), forkedTree.Root()));
        Assert.False(MerkleVerifier.VerifyConsistency(proof, honestTree.RootOfPrefix(10), forkedTree.Root()));
    }

    [Fact]
    public void BadArgumentsThrowOnBuildAndNeverOnVerify()
    {
        MerkleTree tree = new(Hashes(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.InclusionProof(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.InclusionProof(0, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.ConsistencyProof(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.ConsistencyProof(4, 3));
        Assert.False(MerkleVerifier.VerifyInclusion(new byte[32], null!, new byte[32]));
        Assert.False(MerkleVerifier.VerifyConsistency(null!, new byte[32], new byte[32]));
        Assert.False(MerkleVerifier.VerifyInclusion(new byte[32], new MerkleInclusionProof(0, 1, [null!]), new byte[32]));
    }

    private static byte[] Flip(byte[] source, int offset)
    {
        byte[] copy = [.. source];
        copy[offset] ^= 0x01;
        return copy;
    }
}
