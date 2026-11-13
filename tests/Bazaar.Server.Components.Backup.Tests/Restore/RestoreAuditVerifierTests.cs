using Bazaar.Server.Components.Backup.Restore;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Components.Backup.Tests.Restore;

public sealed class RestoreAuditVerifierTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Intact_chain_after_anchor_verifies()
    {
        var rows = BuildChain(6);
        var fixture = Fixture(rows, anchorLastSeq: 3);

        var result = RestoreAuditVerifier.Verify(fixture.Anchor, fixture.After, fixture.Signer, FixedSigner.Signature);

        Assert.Equal(RestoreAuditReason.Ok, result.Reason);
        Assert.True(result.IsValid);
        Assert.Equal(3, result.RowsChecked);
    }

    [Fact]
    public void Tampered_payload_after_anchor_fails_with_hash_mismatch()
    {
        var rows = BuildChain(6);
        var fixture = Fixture(rows, anchorLastSeq: 3);
        fixture.After[1] = fixture.After[1] with { Payload = "tampered" };

        var result = RestoreAuditVerifier.Verify(fixture.Anchor, fixture.After, fixture.Signer, FixedSigner.Signature);

        Assert.Equal(RestoreAuditReason.HashMismatch, result.Reason);
        Assert.Equal(5, result.FailedSeq);
        Assert.Equal(1, result.RowsChecked);
    }

    [Fact]
    public void Wrong_anchor_head_fails_with_link_broken()
    {
        var rows = BuildChain(6);
        var fixture = Fixture(rows, anchorLastSeq: 3);
        var head = (byte[])fixture.Anchor.ChainHeadHash.Clone();
        head[0] ^= 0xFF;
        var anchor = fixture.Anchor with { ChainHeadHash = head };

        var result = RestoreAuditVerifier.Verify(anchor, fixture.After, fixture.Signer, FixedSigner.Signature);

        Assert.Equal(RestoreAuditReason.LinkBroken, result.Reason);
        Assert.Equal(0, result.RowsChecked);
    }

    [Fact]
    public void Changed_root_fails_with_root_mismatch()
    {
        var rows = BuildChain(6);
        var fixture = Fixture(rows, anchorLastSeq: 3);
        var root = (byte[])fixture.Anchor.MerkleRoot.Clone();
        root[31] ^= 0x01;
        var anchor = fixture.Anchor with { MerkleRoot = root };

        var result = RestoreAuditVerifier.Verify(anchor, fixture.After, fixture.Signer, FixedSigner.Signature);

        Assert.Equal(RestoreAuditReason.RootMismatch, result.Reason);
        Assert.Equal(3, result.RowsChecked);
    }

    [Fact]
    public void Bad_signature_fails_with_signature_invalid()
    {
        var rows = BuildChain(6);
        var fixture = Fixture(rows, anchorLastSeq: 3);

        var result = RestoreAuditVerifier.Verify(fixture.Anchor, fixture.After, fixture.Signer, [9, 9, 9]);

        Assert.Equal(RestoreAuditReason.SignatureInvalid, result.Reason);
        Assert.Equal(0, result.RowsChecked);
    }

    [Fact]
    public void Gap_in_sequence_fails_with_sequence_broken()
    {
        var rows = BuildChain(6);
        var fixture = Fixture(rows, anchorLastSeq: 3);
        fixture.After.RemoveAt(1);

        var result = RestoreAuditVerifier.Verify(fixture.Anchor, fixture.After, fixture.Signer, FixedSigner.Signature);

        Assert.Equal(RestoreAuditReason.SequenceBroken, result.Reason);
        Assert.Equal(6, result.FailedSeq);
        Assert.Equal(1, result.RowsChecked);
    }

    [Fact]
    public void Empty_rows_after_anchor_verify_with_zero_rows_checked()
    {
        var rows = BuildChain(6);
        var emptyRoot = new MerkleTree([]).Root;
        var anchor = Anchor(rows[5], emptyRoot);

        var result = RestoreAuditVerifier.Verify(anchor, [], new FixedSigner(), FixedSigner.Signature);

        Assert.Equal(RestoreAuditReason.Ok, result.Reason);
        Assert.Equal(0, result.RowsChecked);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var rows = BuildChain(6);
        var fixture = Fixture(rows, anchorLastSeq: 3);

        Assert.Throws<ArgumentNullException>(() => RestoreAuditVerifier.Verify(null!, fixture.After, fixture.Signer, FixedSigner.Signature));
        Assert.Throws<ArgumentNullException>(() => RestoreAuditVerifier.Verify(fixture.Anchor, null!, fixture.Signer, FixedSigner.Signature));
        Assert.Throws<ArgumentNullException>(() => RestoreAuditVerifier.Verify(fixture.Anchor, fixture.After, null!, FixedSigner.Signature));
        Assert.Throws<ArgumentNullException>(() => RestoreAuditVerifier.Verify(fixture.Anchor, fixture.After, fixture.Signer, null!));
    }

    /// <summary>A chain built with <see cref="AuditChain.Append"/>, seq 1 to <paramref name="count"/>.</summary>
    private static List<AuditRecord> BuildChain(int count)
    {
        var rows = new List<AuditRecord>();
        AuditRecord? head = null;
        for (var i = 1; i <= count; i++)
        {
            head = AuditChain.Append(head, _start.AddSeconds(i), "agent", "turn.post", $"payload-{i}");
            rows.Add(head);
        }

        return rows;
    }

    /// <summary>
    /// Anchor after row <paramref name="anchorLastSeq"/>. Its root is over the rows after it (rows 4 to 6 here), which is
    /// the set <see cref="RestoreAuditVerifier"/> checks in step (d).
    /// </summary>
    private static (AuditCheckpoint Anchor, List<AuditRecord> After, FixedSigner Signer) Fixture(List<AuditRecord> rows, int anchorLastSeq)
    {
        var after = rows.Skip(anchorLastSeq).ToList();
        var root = new MerkleTree(after.Select(r => r.Hash)).Root;
        return (Anchor(rows[anchorLastSeq - 1], root), after, new FixedSigner());
    }

    private static AuditCheckpoint Anchor(AuditRecord last, byte[] root) =>
        new(FirstSeq: 1, LastSeq: last.Seq, RowCount: last.Seq, ChainHeadHash: last.Hash, MerkleRoot: root, CreatedAt: _start);

    /// <summary>Signs with a fixed value and verifies by signature bytes, so the tests control which step fails.</summary>
    private sealed class FixedSigner : IAuditCheckpointSigner
    {
        public static readonly byte[] Signature = [1, 2, 3, 4];

        public string KeyId => "test-key";

        public byte[] Sign(ReadOnlyMemory<byte> data) => Signature.ToArray();

        public bool Verify(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> signature) => signature.Span.SequenceEqual(Signature);
    }
}
