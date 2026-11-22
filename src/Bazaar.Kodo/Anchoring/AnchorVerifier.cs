using Bazaar.Kodo.Journal;

namespace Bazaar.Kodo.Anchoring;

/// <summary>Result class of an anchor check. There is deliberately no "green when unsure".</summary>
public enum AnchorVerdict
{
    /// <summary>The history matches the anchor.</summary>
    Verified,

    /// <summary>The history provably differs from the anchor.</summary>
    Tampered,

    /// <summary>The check could not be completed. Never shown as healthy.</summary>
    Unknown,
}

/// <summary>Why a verdict was reached.</summary>
public enum AnchorFinding
{
    /// <summary>The anchored prefix matches.</summary>
    Matches,

    /// <summary>The anchor record itself is malformed, so nothing can be concluded.</summary>
    MalformedAnchor,

    /// <summary>The supplied records or proof are malformed (for example a sequence gap), so nothing can be concluded from them.</summary>
    MalformedInput,

    /// <summary>The journal hash chain is broken.</summary>
    ChainBroken,

    /// <summary>Fewer records are present than were anchored.</summary>
    Truncated,

    /// <summary>The records inside the anchored range hash to a different root or head.</summary>
    RootMismatch,

    /// <summary>A consistency proof failed to link two anchors.</summary>
    ProofRejected,
}

/// <summary>Verdict plus its reason.</summary>
public sealed record AnchorCheck(AnchorVerdict Verdict, AnchorFinding Finding);

/// <summary>
/// Detects a rewritten history by comparing a stored anchor to the current chain (doc 46 section 6.5.1).
/// Anything that cannot be checked is <see cref="AnchorVerdict.Unknown"/>, never Verified.
/// </summary>
public static class AnchorVerifier
{
    /// <summary>
    /// Checks the full current journal (from sequence 0) against <paramref name="anchor"/>. Records after the anchored
    /// range are allowed: they are new history.
    /// </summary>
    public static AnchorCheck VerifyAgainstJournal(AnchorRecord anchor, IReadOnlyList<JournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (anchor is not { IsWellFormed: true })
        {
            return new AnchorCheck(AnchorVerdict.Unknown, AnchorFinding.MalformedAnchor);
        }

        if (records.Count < anchor.TreeSize)
        {
            // A journal that lost anchored records is a rewrite, not an unknown: the anchor proves they existed.
            return new AnchorCheck(AnchorVerdict.Tampered, AnchorFinding.Truncated);
        }

        foreach (JournalRecord record in records)
        {
            if (record is null || record.Hash is not { Length: JournalChain.HashLength })
            {
                return new AnchorCheck(AnchorVerdict.Unknown, AnchorFinding.MalformedInput);
            }
        }

        if (!JournalChain.Verify(records).IsValid)
        {
            return new AnchorCheck(AnchorVerdict.Tampered, AnchorFinding.ChainBroken);
        }

        int size = checked((int)anchor.TreeSize);
        byte[] head = records[size - 1].Hash;
        MerkleTree tree = new(records.Take(size).Select(r => r.Hash));
        bool matches = head.AsSpan().SequenceEqual(anchor.JournalHead) && tree.Root().AsSpan().SequenceEqual(anchor.Root);
        return matches
            ? new AnchorCheck(AnchorVerdict.Verified, AnchorFinding.Matches)
            : new AnchorCheck(AnchorVerdict.Tampered, AnchorFinding.RootMismatch);
    }

    /// <summary>
    /// Checks that <paramref name="newer"/> extends <paramref name="older"/> using a consistency proof, for when the
    /// full journal is not at hand. A bad proof is Unknown, not Tampered: the proof itself may be the damaged part.
    /// </summary>
    public static AnchorCheck VerifyAnchors(AnchorRecord older, AnchorRecord newer, MerkleConsistencyProof proof)
    {
        if (older is not { IsWellFormed: true } || newer is not { IsWellFormed: true })
        {
            return new AnchorCheck(AnchorVerdict.Unknown, AnchorFinding.MalformedAnchor);
        }

        if (proof is null || proof.FirstSize != older.TreeSize || proof.SecondSize != newer.TreeSize)
        {
            return new AnchorCheck(AnchorVerdict.Unknown, AnchorFinding.MalformedInput);
        }

        return MerkleVerifier.VerifyConsistency(proof, older.Root, newer.Root)
            ? new AnchorCheck(AnchorVerdict.Verified, AnchorFinding.Matches)
            : new AnchorCheck(AnchorVerdict.Unknown, AnchorFinding.ProofRejected);
    }
}
