using System.Security.Cryptography;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Security.Tests.Audit.Checkpoints;

public class AuditProofTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    private readonly HmacTestSigner _signer = new();

    private static List<AuditRecord> Chain(int count)
    {
        var records = new List<AuditRecord>();
        AuditRecord? head = null;
        for (var i = 0; i < count; i++)
        {
            head = AuditChain.Append(head, _at.AddSeconds(i), $"actor-{i % 3}", "setting.change", $"payload {i}");
            records.Add(head);
        }

        return records;
    }

    private (AuditCheckpoint Checkpoint, byte[] Signature) Checkpoint(List<AuditRecord> records)
    {
        var tree = new MerkleTree(records.Select(r => r.Hash));
        var checkpoint = new AuditCheckpoint(
            records[0].Seq, records[^1].Seq, records.Count, records[^1].Hash, tree.Root, _at.AddHours(1));
        return (checkpoint, _signer.Sign(checkpoint.SignedBytes()));
    }

    private AuditProofDocument Proof(List<AuditRecord> records, long seq)
    {
        var (checkpoint, signature) = Checkpoint(records);
        return AuditProofBuilder.Build(checkpoint, _signer.KeyId, signature, records, seq);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(11)]
    public void Every_row_proof_verifies_including_after_a_json_round_trip(int count)
    {
        var records = Chain(count);

        foreach (var record in records)
        {
            var document = Proof(records, record.Seq);
            Assert.Equal(AuditProofFile.FormatId, document.Format);
            Assert.True(AuditProofVerifier.Verify(document, _signer).IsValid);

            var json = AuditProofFile.Write(document);
            Assert.True(AuditProofVerifier.Verify(json, _signer).IsValid);
        }
    }

    [Fact]
    public void Proof_for_a_range_that_does_not_start_at_one_verifies()
    {
        var records = Chain(9).Skip(4).ToList();

        foreach (var record in records)
        {
            Assert.True(AuditProofVerifier.Verify(Proof(records, record.Seq), _signer).IsValid);
        }
    }

    [Fact]
    public void Json_uses_the_format_id()
    {
        var json = AuditProofFile.Write(Proof(Chain(3), 2));

        Assert.Contains("\"bazaar.audit.proof.v1\"", json);
    }

    [Fact]
    public void Tampered_payload_fails()
    {
        var document = Proof(Chain(6), 3);
        var tampered = document with { Row = document.Row with { Payload = "something else" } };

        Assert.False(AuditProofVerifier.Verify(tampered, _signer).IsValid);
    }

    [Fact]
    public void Tampered_sibling_fails()
    {
        var document = Proof(Chain(6), 3);
        var siblings = document.Siblings.Select(s => (byte[])s.Clone()).ToList();
        siblings[0][0] ^= 0xFF;

        Assert.False(AuditProofVerifier.Verify(document with { Siblings = siblings }, _signer).IsValid);
    }

    [Fact]
    public void Missing_or_extra_sibling_fails()
    {
        var document = Proof(Chain(6), 3);

        Assert.False(AuditProofVerifier.Verify(document with { Siblings = document.Siblings.Skip(1).ToList() }, _signer).IsValid);
        Assert.False(AuditProofVerifier.Verify(document with { Siblings = [.. document.Siblings, new byte[32]] }, _signer).IsValid);
    }

    [Fact]
    public void Build_refuses_a_row_outside_the_range()
    {
        var records = Chain(5);
        var (checkpoint, signature) = Checkpoint(records);

        Assert.Throws<ArgumentException>(() => AuditProofBuilder.Build(checkpoint, _signer.KeyId, signature, records, 0));
        Assert.Throws<ArgumentException>(() => AuditProofBuilder.Build(checkpoint, _signer.KeyId, signature, records, 6));
    }

    [Fact]
    public void Build_refuses_records_that_do_not_reproduce_the_root()
    {
        var records = Chain(5);
        var (checkpoint, signature) = Checkpoint(records);
        var other = Chain(5);
        other[2] = AuditChain.Append(other[1], _at, "x", "y", "different");

        Assert.Throws<ArgumentException>(() => AuditProofBuilder.Build(checkpoint, _signer.KeyId, signature, other, 2));
    }

    [Fact]
    public void Seq_moved_outside_the_range_fails()
    {
        var document = Proof(Chain(5), 3);
        var moved = document with { Row = document.Row with { Seq = 9 } };

        Assert.False(AuditProofVerifier.Verify(moved, _signer).IsValid);
    }

    [Fact]
    public void Wrong_signature_fails()
    {
        var document = Proof(Chain(5), 3);
        var signature = (byte[])document.Signature.Clone();
        signature[0] ^= 0xFF;

        Assert.False(AuditProofVerifier.Verify(document with { Signature = signature }, _signer).IsValid);
        Assert.False(AuditProofVerifier.Verify(document with { Signature = [] }, _signer).IsValid);
    }

    [Fact]
    public void Row_count_inconsistent_with_the_range_fails()
    {
        var document = Proof(Chain(5), 3);
        var bad = document with { Checkpoint = document.Checkpoint with { RowCount = 6 } };

        var result = AuditProofVerifier.Verify(bad, _signer);

        Assert.False(result.IsValid);
        Assert.Contains("row_count", result.Reason);
    }

    [Fact]
    public void Unknown_format_fails()
    {
        var document = Proof(Chain(3), 1);

        Assert.False(AuditProofVerifier.Verify(document with { Format = "bazaar.audit.proof.v2" }, _signer).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"bazaar.audit.proof.v1\",\"row\":{\"seq\":\"x\"}}")]
    [InlineData("{\"format\":\"bazaar.audit.proof.v1\",\"signature\":\"%%%\"}")]
    public void Malformed_json_returns_invalid_without_throwing(string? json)
    {
        var result = AuditProofVerifier.Verify(json, _signer);

        Assert.False(result.IsValid);
    }

    /// <summary>Test double only. Uses a fixed key in test code; no real key material is involved.</summary>
    private sealed class HmacTestSigner : IAuditCheckpointSigner
    {
        private static readonly byte[] _key = "test-key-not-a-secret"u8.ToArray();

        public string KeyId => "test";

        public byte[] Sign(ReadOnlyMemory<byte> data) => HMACSHA256.HashData(_key, data.Span);

        public bool Verify(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> signature) =>
            CryptographicOperations.FixedTimeEquals(Sign(data), signature.Span);
    }
}
