using System.Security.Cryptography;

namespace Bazaar.Kodo.Anchoring;

/// <summary>
/// Domain-separated hashing for the journal Merkle tree (doc 46 section 6.5.1). Leaf = SHA-256(0x00 || data),
/// node = SHA-256(0x01 || left || right). The prefixes make a leaf impossible to read as an inner node, which blocks
/// the second-preimage trick of presenting an inner node as a leaf.
/// </summary>
public static class MerkleHasher
{
    /// <summary>Length of every hash in the tree, in bytes.</summary>
    public const int HashLength = 32;

    private const byte _leafPrefix = 0x00;
    private const byte _nodePrefix = 0x01;

    /// <summary>Root of the empty tree: SHA-256 of no input. No leaf or node hash can equal it (they hash a prefix byte).</summary>
    public static byte[] EmptyRoot() => SHA256.HashData(ReadOnlySpan<byte>.Empty);

    /// <summary>Hashes one leaf's data (a journal record hash).</summary>
    public static byte[] HashLeaf(ReadOnlySpan<byte> data)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([_leafPrefix]);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }

    /// <summary>Hashes an inner node from its two children.</summary>
    /// <exception cref="ArgumentException">A child is not 32 bytes.</exception>
    public static byte[] HashNode(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != HashLength || right.Length != HashLength)
        {
            throw new ArgumentException($"Child hashes must be {HashLength} bytes.");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([_nodePrefix]);
        hash.AppendData(left);
        hash.AppendData(right);
        return hash.GetHashAndReset();
    }
}
