using Bazaar.Kodo.Anchoring;

namespace Bazaar.Server.Kodo.Postgres;

/// <summary>Builds the journal Merkle proof from record hashes. Pure logic: the store supplies the hashes.</summary>
internal static class KodoJournalProofs
{
    /// <summary>Most leaves one proof reads. A larger history gets no proof until checkpoint-based proofs exist (doc 46 section 6.5).</summary>
    public const int MaxLeaves = 1_000_000;

    /// <summary>
    /// The proof for <paramref name="sequence"/> over <paramref name="hashes"/>, which must be the hashes of records 0 to n-1 in order.
    /// Null when the sequence is outside them.
    /// </summary>
    public static KodoJournalProofView? Build(long sequence, IReadOnlyList<byte[]> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        if (sequence < 0 || sequence >= hashes.Count)
        {
            return null;
        }

        MerkleTree tree = new(hashes);
        MerkleInclusionProof proof = tree.InclusionProof((int)sequence, tree.Size);
        return new KodoJournalProofView(
            sequence,
            proof.TreeSize,
            Hex(hashes[(int)sequence]),
            Hex(tree.Root()),
            proof.Path.Select(Hex).ToArray());
    }

    private static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);
}
