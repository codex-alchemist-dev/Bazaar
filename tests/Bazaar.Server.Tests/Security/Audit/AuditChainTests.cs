using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Tests.Security.Audit;

public class AuditChainTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static List<AuditRecord> BuildChain(int count)
    {
        var records = new List<AuditRecord>();
        AuditRecord? head = null;
        for (var i = 0; i < count; i++)
        {
            head = AuditChain.Append(head, Start.AddSeconds(i), "agent:charles", "tool.call", $"{{\"n\":{i}}}");
            records.Add(head);
        }

        return records;
    }

    private static byte[] FlipFirstByte(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        copy[0] ^= 0x01;
        return copy;
    }

    [Fact]
    public void Append_starts_at_seq_1_with_genesis_prev_hash()
    {
        var first = AuditChain.Append(null, Start, "system", "boot", "{}");

        Assert.Equal(1, first.Seq);
        Assert.Equal(AuditChain.GenesisHash, first.PrevHash);
        Assert.Equal(32, first.Hash.Length);
    }

    [Fact]
    public void Append_links_each_record_to_the_previous_hash()
    {
        var records = BuildChain(3);

        Assert.Equal(new long[] { 1, 2, 3 }, records.Select(r => r.Seq));
        Assert.Equal(records[0].Hash, records[1].PrevHash);
        Assert.Equal(records[1].Hash, records[2].PrevHash);
    }

    [Fact]
    public void Verify_accepts_an_untouched_chain()
    {
        var result = AuditChain.Verify(BuildChain(5));

        Assert.True(result.IsValid);
        Assert.Equal(5, result.Checked);
    }

    [Fact]
    public void Verify_accepts_an_empty_chain()
    {
        Assert.True(AuditChain.Verify([]).IsValid);
    }

    [Fact]
    public void Verify_detects_one_byte_changed_in_a_payload()
    {
        var records = BuildChain(4);
        records[2] = records[2] with { Payload = records[2].Payload.Replace("2", "9") };

        var result = AuditChain.Verify(records);

        Assert.False(result.IsValid);
        Assert.Equal(AuditFailure.HashMismatch, result.Failure);
        Assert.Equal(2, result.FailedIndex);
        Assert.Equal(3, result.FailedSeq);
    }

    [Fact]
    public void Verify_detects_one_byte_changed_in_a_stored_hash()
    {
        var records = BuildChain(3);
        records[1] = records[1] with { Hash = FlipFirstByte(records[1].Hash) };

        var result = AuditChain.Verify(records);

        Assert.False(result.IsValid);
        // Record 1 no longer matches its own hash, and record 2 no longer links to it.
        Assert.Equal(AuditFailure.HashMismatch, result.Failure);
        Assert.Equal(1, result.FailedIndex);
    }

    [Fact]
    public void Verify_detects_one_byte_changed_in_a_prev_hash_link()
    {
        var records = BuildChain(3);
        records[2] = records[2] with { PrevHash = FlipFirstByte(records[2].PrevHash) };

        var result = AuditChain.Verify(records);

        Assert.False(result.IsValid);
        Assert.Equal(AuditFailure.PrevHashMismatch, result.Failure);
        Assert.Equal(2, result.FailedIndex);
    }

    [Fact]
    public void Verify_detects_reordered_records()
    {
        var records = BuildChain(3);
        (records[0], records[1]) = (records[1], records[0]);

        var result = AuditChain.Verify(records);

        Assert.False(result.IsValid);
        Assert.Equal(AuditFailure.SequenceBroken, result.Failure);
        Assert.Equal(0, result.FailedIndex);
    }

    [Fact]
    public void Verify_detects_a_missing_middle_record()
    {
        var records = BuildChain(3);
        records.RemoveAt(1);

        var result = AuditChain.Verify(records);

        Assert.False(result.IsValid);
        Assert.Equal(AuditFailure.SequenceBroken, result.Failure);
    }

    [Fact]
    public void Verify_detects_a_truncated_head()
    {
        var records = BuildChain(3);
        records.RemoveAt(0);

        var result = AuditChain.Verify(records);

        Assert.False(result.IsValid);
        Assert.Equal(AuditFailure.SequenceBroken, result.Failure);
    }

    [Fact]
    public void Verify_without_an_anchor_cannot_see_tail_truncation()
    {
        // A chain cut at the tail is still internally consistent; only an external head hash exposes it.
        var records = BuildChain(3);
        records.RemoveAt(2);

        Assert.True(AuditChain.Verify(records).IsValid);
    }

    [Fact]
    public void Verify_with_the_expected_head_detects_tail_truncation()
    {
        var records = BuildChain(3);
        var anchor = records[2].Hash;
        records.RemoveAt(2);

        var result = AuditChain.Verify(records, anchor);

        Assert.False(result.IsValid);
        Assert.Equal(AuditFailure.HeadMismatch, result.Failure);
    }

    [Fact]
    public void Verify_with_the_expected_head_accepts_the_full_chain()
    {
        var records = BuildChain(3);

        Assert.True(AuditChain.Verify(records, records[2].Hash).IsValid);
    }
}
