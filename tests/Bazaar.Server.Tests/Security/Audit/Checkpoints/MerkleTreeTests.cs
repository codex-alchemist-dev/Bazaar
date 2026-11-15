using System.Security.Cryptography;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Tests.Security.Audit.Checkpoints;

public class MerkleTreeTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Leaf(int n) => SHA256.HashData(BitConverter.GetBytes(n));

    private static List<byte[]> Leaves(int count) => Enumerable.Range(0, count).Select(Leaf).ToList();

    private static byte[] FlipFirstByte(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        copy[0] ^= 0x01;
        return copy;
    }

    [Fact]
    public void Empty_tree_root_is_sha256_of_empty_input()
    {
        var tree = new MerkleTree([]);

        Assert.Equal(0, tree.LeafCount);
        Assert.Equal(SHA256.HashData(ReadOnlySpan<byte>.Empty), tree.Root);
    }

    [Fact]
    public void Empty_tree_has_no_inclusion_proof()
    {
        var tree = new MerkleTree([]);

        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetInclusionProof(0));
    }

    [Fact]
    public void Single_leaf_root_is_the_domain_separated_leaf_hash()
    {
        var leaf = Leaf(1);
        var tree = new MerkleTree([leaf]);

        var expected = SHA256.HashData([0x00, .. leaf]);
        Assert.Equal(expected, tree.Root);
        Assert.Empty(tree.GetInclusionProof(0).Siblings);
        Assert.True(MerkleTree.Verify(leaf, 0, tree.GetInclusionProof(0), tree.Root));
    }

    [Fact]
    public void Two_leaves_root_hashes_the_two_leaves_as_one_node()
    {
        var a = Leaf(1);
        var b = Leaf(2);
        var tree = new MerkleTree([a, b]);

        var leafA = SHA256.HashData([0x00, .. a]);
        var leafB = SHA256.HashData([0x00, .. b]);
        Assert.Equal(SHA256.HashData([0x01, .. leafA, .. leafB]), tree.Root);
    }

    [Fact]
    public void Odd_node_is_promoted_unchanged_not_paired_with_itself()
    {
        var leaves = Leaves(3);
        var tree = new MerkleTree(leaves);

        var leafA = SHA256.HashData([0x00, .. leaves[0]]);
        var leafB = SHA256.HashData([0x00, .. leaves[1]]);
        var leafC = SHA256.HashData([0x00, .. leaves[2]]);
        var left = SHA256.HashData([0x01, .. leafA, .. leafB]);
        // The third leaf has no sibling at level 0, so it moves up as-is.
        Assert.Equal(SHA256.HashData([0x01, .. left, .. leafC]), tree.Root);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(10)]
    public void Every_leaf_proof_verifies(int count)
    {
        var leaves = Leaves(count);
        var tree = new MerkleTree(leaves);

        for (var i = 0; i < count; i++)
        {
            var proof = tree.GetInclusionProof(i);
            Assert.True(MerkleTree.Verify(leaves[i], i, proof, tree.Root), $"leaf {i} of {count}");
        }
    }

    [Fact]
    public void Five_leaves_last_leaf_proof_skips_promoted_levels()
    {
        var leaves = Leaves(5);
        var tree = new MerkleTree(leaves);

        var proof = tree.GetInclusionProof(4);

        Assert.Equal(5, proof.TreeSize);
        // Leaf 4 is promoted at level 0 and again at level 1 (no right neighbour either time), so only the
        // level-2 pair adds a sibling.
        Assert.Single(proof.Siblings);
        Assert.True(MerkleTree.Verify(leaves[4], 4, proof, tree.Root));
    }

    [Fact]
    public void Tampered_leaf_fails_verification()
    {
        var leaves = Leaves(5);
        var tree = new MerkleTree(leaves);

        for (var i = 0; i < leaves.Count; i++)
        {
            var proof = tree.GetInclusionProof(i);
            Assert.False(MerkleTree.Verify(FlipFirstByte(leaves[i]), i, proof, tree.Root), $"leaf {i}");
        }
    }

    [Fact]
    public void Wrong_index_fails_verification()
    {
        var leaves = Leaves(5);
        var tree = new MerkleTree(leaves);

        for (var i = 0; i < leaves.Count; i++)
        {
            var proof = tree.GetInclusionProof(i);
            for (var other = 0; other < leaves.Count; other++)
            {
                if (other == i)
                {
                    continue;
                }

                Assert.False(MerkleTree.Verify(leaves[i], other, proof, tree.Root), $"leaf {i} claimed at {other}");
            }
        }

        Assert.False(MerkleTree.Verify(leaves[0], -1, tree.GetInclusionProof(0), tree.Root));
        Assert.False(MerkleTree.Verify(leaves[0], 5, tree.GetInclusionProof(0), tree.Root));
    }

    [Fact]
    public void Wrong_root_fails_verification()
    {
        var leaves = Leaves(5);
        var tree = new MerkleTree(leaves);

        var proof = tree.GetInclusionProof(2);

        Assert.False(MerkleTree.Verify(leaves[2], 2, proof, FlipFirstByte(tree.Root)));
    }

    [Fact]
    public void Tampered_sibling_fails_verification()
    {
        var leaves = Leaves(5);
        var tree = new MerkleTree(leaves);
        var proof = tree.GetInclusionProof(1);
        var siblings = proof.Siblings.ToList();
        siblings[0] = FlipFirstByte(siblings[0]);

        Assert.False(MerkleTree.Verify(leaves[1], 1, proof with { Siblings = siblings }, tree.Root));
    }

    [Fact]
    public void Proof_with_extra_or_missing_siblings_fails_verification()
    {
        var leaves = Leaves(5);
        var tree = new MerkleTree(leaves);
        var proof = tree.GetInclusionProof(0);

        Assert.False(MerkleTree.Verify(leaves[0], 0, proof with { Siblings = [.. proof.Siblings, Leaf(99)] }, tree.Root));
        Assert.False(MerkleTree.Verify(leaves[0], 0, proof with { Siblings = proof.Siblings.Take(proof.Siblings.Count - 1).ToList() }, tree.Root));
    }

    [Fact]
    public void Malformed_hash_lengths_fail_verification_without_throwing()
    {
        var leaves = Leaves(2);
        var tree = new MerkleTree(leaves);
        var proof = tree.GetInclusionProof(0);

        Assert.False(MerkleTree.Verify(new byte[31], 0, proof, tree.Root));
        Assert.False(MerkleTree.Verify(leaves[0], 0, proof, new byte[16]));
    }

    [Fact]
    public void Constructor_rejects_leaves_that_are_not_sha256_sized()
    {
        Assert.Throws<ArgumentException>(() => new MerkleTree([new byte[31]]));
    }

    [Fact]
    public void Ten_records_from_audit_chain_append_merkle_verify()
    {
        var records = new List<AuditRecord>();
        AuditRecord? head = null;
        for (var i = 0; i < 10; i++)
        {
            head = AuditChain.Append(head, _start.AddSeconds(i), "agent:charles", "tool.call", $"{{\"n\":{i}}}");
            records.Add(head);
        }

        var tree = new MerkleTree(records.Select(r => r.Hash));

        Assert.Equal(10, tree.LeafCount);
        for (var i = 0; i < records.Count; i++)
        {
            Assert.True(MerkleTree.Verify(records[i].Hash, i, tree.GetInclusionProof(i), tree.Root));
        }
    }
}
