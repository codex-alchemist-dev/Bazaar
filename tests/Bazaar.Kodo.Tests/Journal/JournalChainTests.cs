using Bazaar.Kodo.Journal;

namespace Bazaar.Kodo.Tests.Journal;

public sealed class JournalChainTests
{
    [Fact]
    public void CleanChainVerifies()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 1, count: 100);

        JournalVerification result = JournalChain.Verify(records);

        Assert.True(result.IsValid);
        Assert.Null(result.FirstBrokenIndex);
    }

    [Fact]
    public void EmptyChainVerifies()
    {
        Assert.True(JournalChain.Verify([]).IsValid);
    }

    [Fact]
    public void FirstRecordLinksToGenesisHash()
    {
        JournalRecord first = JournalSamples.Chain(seed: 2, count: 1)[0];

        Assert.Equal(JournalChain.GenesisHash(), first.PreviousHash);
        Assert.Equal(JournalChain.HashLength, first.Hash.Length);
    }

    [Fact]
    public void AlteringOneByteOfAnyRecordIsDetectedAtThatIndex()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 7, count: 12);

        for (int index = 0; index < records.Count; index++)
        {
            JournalRecord target = records[index];
            foreach (JournalRecord altered in AlteredCopies(target))
            {
                List<JournalRecord> tampered = [.. records];
                tampered[index] = altered;

                JournalVerification result = JournalChain.Verify(tampered);

                Assert.Equal(index, result.FirstBrokenIndex);
            }
        }
    }

    [Fact]
    public void ReorderedRecordsAreDetected()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 9, count: 6);
        (records[2], records[3]) = (records[3], records[2]);

        Assert.Equal(2, JournalChain.Verify(records).FirstBrokenIndex);
    }

    [Fact]
    public void CanonicalEncodingRoundTrips()
    {
        foreach (JournalRecord record in JournalSamples.Chain(seed: 4, count: 20))
        {
            byte[] canonical = JournalCodec.Encode(record);

            JournalRecord decoded = JournalCodec.Decode(canonical, record.PreviousHash, record.Hash);

            Assert.Equal(record.Sequence, decoded.Sequence);
            Assert.Equal(record.At, decoded.At);
            Assert.Equal(record.Kind, decoded.Kind);
            Assert.Equal(record.Payload, decoded.Payload);
            Assert.Equal(canonical, JournalCodec.Encode(decoded));
        }
    }

    [Fact]
    public void EncodingRejectsEmptyKind()
    {
        JournalRecord record = JournalSamples.Chain(seed: 4, count: 1)[0] with { Kind = string.Empty };

        Assert.Throws<ArgumentException>(() => JournalCodec.Encode(record));
    }

    [Fact]
    public void DecodingRejectsTruncatedBytes()
    {
        byte[] canonical = JournalCodec.Encode(JournalSamples.Chain(seed: 4, count: 1)[0]);

        Assert.Throws<InvalidDataException>(() => JournalCodec.Decode(canonical.AsSpan(0, canonical.Length - 1), [], []));
    }

    /// <summary>Property check over fixed seeds: any single-byte change to any record is caught at that record.</summary>
    [Theory]
    [InlineData(11)]
    [InlineData(22)]
    [InlineData(33)]
    [InlineData(44)]
    [InlineData(55)]
    [InlineData(66)]
    [InlineData(77)]
    [InlineData(88)]
    public void SeededChainsDetectAnySingleByteChange(int seed)
    {
        List<JournalRecord> records = JournalSamples.Chain(seed, count: 30);
        Assert.True(JournalChain.Verify(records).IsValid);

        // A different fixed stream picks which field and byte to flip for each record.
#pragma warning disable CA5394
        Random picker = new(seed + 1000);
#pragma warning restore CA5394
        for (int index = 0; index < records.Count; index++)
        {
            JournalRecord target = records[index];
            JournalRecord altered = picker.Next(3) switch
            {
                0 when target.Payload.Length > 0 => target with { Payload = JournalSamples.FlipByte(target.Payload, picker.Next(target.Payload.Length)) },
                1 => target with { Hash = JournalSamples.FlipByte(target.Hash, picker.Next(target.Hash.Length)) },
                _ => target with { PreviousHash = JournalSamples.FlipByte(target.PreviousHash, picker.Next(target.PreviousHash.Length)) },
            };

            List<JournalRecord> tampered = [.. records];
            tampered[index] = altered;

            Assert.Equal(index, JournalChain.Verify(tampered).FirstBrokenIndex);
        }
    }

    private static IEnumerable<JournalRecord> AlteredCopies(JournalRecord target)
    {
        for (int offset = 0; offset < target.Payload.Length; offset++)
        {
            yield return target with { Payload = JournalSamples.FlipByte(target.Payload, offset) };
        }

        for (int offset = 0; offset < target.Hash.Length; offset++)
        {
            yield return target with { Hash = JournalSamples.FlipByte(target.Hash, offset) };
        }

        for (int offset = 0; offset < target.PreviousHash.Length; offset++)
        {
            yield return target with { PreviousHash = JournalSamples.FlipByte(target.PreviousHash, offset) };
        }
    }
}
