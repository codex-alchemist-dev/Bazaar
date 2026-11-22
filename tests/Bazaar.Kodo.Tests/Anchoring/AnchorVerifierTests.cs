using Bazaar.Kodo.Anchoring;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Tests.Journal;

namespace Bazaar.Kodo.Tests.Anchoring;

public sealed class AnchorVerifierTests
{
    [Fact]
    public void UntouchedJournalVerifiesIncludingRecordsAfterTheAnchor()
    {
        List<JournalRecord> records = JournalSamples.Chain(3, 60);
        AnchorRecord anchor = AnchorRecordTests.AnchorOf(records[..40]);

        AnchorCheck check = AnchorVerifier.VerifyAgainstJournal(anchor, records);

        Assert.Equal(AnchorVerdict.Verified, check.Verdict);
        Assert.Equal(AnchorFinding.Matches, check.Finding);
    }

    [Fact]
    public void RewrittenHistoryWithRecomputedHashesIsDetected()
    {
        // An operator edits record 10 and recomputes the whole chain so the chain itself is valid.
        List<JournalRecord> original = JournalSamples.Chain(4, 30);
        AnchorRecord anchor = AnchorRecordTests.AnchorOf(original);
        List<JournalRecord> rewritten = [];
        byte[] previous = JournalChain.GenesisHash();
        foreach (JournalRecord record in original)
        {
            byte[] payload = record.Sequence == 10 ? [1, 2, 3] : record.Payload;
            JournalRecord copy = JournalChain.Create(record.Sequence, record.At, record.Kind, payload, previous);
            rewritten.Add(copy);
            previous = copy.Hash;
        }

        Assert.True(JournalChain.Verify(rewritten).IsValid);
        AnchorCheck check = AnchorVerifier.VerifyAgainstJournal(anchor, rewritten);

        Assert.Equal(AnchorVerdict.Tampered, check.Verdict);
        Assert.Equal(AnchorFinding.RootMismatch, check.Finding);
    }

    [Fact]
    public void BrokenChainIsTampered()
    {
        List<JournalRecord> records = JournalSamples.Chain(5, 20);
        AnchorRecord anchor = AnchorRecordTests.AnchorOf(records);
        records[7] = records[7] with { Payload = [9, 9, 9] };

        AnchorCheck check = AnchorVerifier.VerifyAgainstJournal(anchor, records);

        Assert.Equal(AnchorVerdict.Tampered, check.Verdict);
        Assert.Equal(AnchorFinding.ChainBroken, check.Finding);
    }

    [Fact]
    public void TruncatedJournalIsTampered()
    {
        List<JournalRecord> records = JournalSamples.Chain(6, 20);
        AnchorRecord anchor = AnchorRecordTests.AnchorOf(records);

        AnchorCheck check = AnchorVerifier.VerifyAgainstJournal(anchor, records[..19]);

        Assert.Equal(AnchorVerdict.Tampered, check.Verdict);
        Assert.Equal(AnchorFinding.Truncated, check.Finding);
    }

    [Fact]
    public void AnchorWithWrongHeadIsTampered()
    {
        List<JournalRecord> records = JournalSamples.Chain(7, 20);
        AnchorRecord anchor = AnchorRecordTests.AnchorOf(records) with { JournalHead = new byte[32] };

        Assert.Equal(AnchorVerdict.Tampered, AnchorVerifier.VerifyAgainstJournal(anchor, records).Verdict);
    }

    [Fact]
    public void MalformedAnchorIsUnknownNeverVerified()
    {
        List<JournalRecord> records = JournalSamples.Chain(8, 5);
        AnchorRecord good = AnchorRecordTests.AnchorOf(records);

        foreach (AnchorRecord bad in new[] { good with { Root = new byte[5] }, good with { TreeSize = 0 }, good with { Version = 9 }, null! })
        {
            AnchorCheck check = AnchorVerifier.VerifyAgainstJournal(bad, records);
            Assert.Equal(AnchorVerdict.Unknown, check.Verdict);
            Assert.Equal(AnchorFinding.MalformedAnchor, check.Finding);
        }
    }

    [Fact]
    public void MalformedRecordsAreUnknown()
    {
        List<JournalRecord> records = JournalSamples.Chain(9, 5);
        AnchorRecord anchor = AnchorRecordTests.AnchorOf(records);
        records[2] = records[2] with { Hash = [] };

        Assert.Equal(AnchorVerdict.Unknown, AnchorVerifier.VerifyAgainstJournal(anchor, records).Verdict);
    }

    [Fact]
    public void ConsistencyProofLinksTwoAnchors()
    {
        List<JournalRecord> records = JournalSamples.Chain(10, 50);
        AnchorRecord older = AnchorRecordTests.AnchorOf(records[..17]);
        AnchorRecord newer = AnchorRecordTests.AnchorOf(records);
        MerkleTree tree = new(records.Select(r => r.Hash));
        MerkleConsistencyProof proof = tree.ConsistencyProof(17, 50);

        Assert.Equal(AnchorVerdict.Verified, AnchorVerifier.VerifyAnchors(older, newer, proof).Verdict);

        // A forked newer anchor cannot be linked, and a bad proof is Unknown, not Verified.
        AnchorRecord forked = newer with { Root = new byte[32] };
        Assert.Equal(AnchorVerdict.Unknown, AnchorVerifier.VerifyAnchors(older, forked, proof).Verdict);
        Assert.Equal(AnchorVerdict.Unknown, AnchorVerifier.VerifyAnchors(older, newer, proof with { FirstSize = 16 }).Verdict);
        Assert.Equal(AnchorVerdict.Unknown, AnchorVerifier.VerifyAnchors(older, newer, null!).Verdict);
        Assert.Equal(AnchorVerdict.Unknown, AnchorVerifier.VerifyAnchors(older with { TreeSize = 0 }, newer, proof).Verdict);
    }
}
