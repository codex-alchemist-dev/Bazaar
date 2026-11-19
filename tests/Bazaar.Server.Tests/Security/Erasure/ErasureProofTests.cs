using System.Security.Cryptography;
using Bazaar.Server.Security.Erasure;

namespace Bazaar.Server.Tests.Security.Erasure;

public class ErasureProofTests
{
    private static readonly DateTimeOffset _erasedAt = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _backupExpiry = new(2026, 12, 13, 9, 30, 0, TimeSpan.Zero);

    private static byte[] Ref(int n) => SHA256.HashData(BitConverter.GetBytes(n));

    private static ErasureProof Base() => ErasureProof.Create(
        Ref(1),
        ["key-b", "key-a"],
        ["gc-messages", "personal-fields"],
        _backupExpiry,
        _erasedAt,
        "identity-key-1");

    [Fact]
    public void Create_sorts_key_ids_and_scopes()
    {
        var proof = Base();

        Assert.Equal(["key-a", "key-b"], proof.KeyIds);
        Assert.Equal(["gc-messages", "personal-fields"], proof.Scopes);
    }

    [Fact]
    public void Create_ignores_the_order_of_inputs()
    {
        var reordered = ErasureProof.Create(
            Ref(1),
            ["key-a", "key-b"],
            ["personal-fields", "gc-messages"],
            _backupExpiry,
            _erasedAt,
            "identity-key-1");

        Assert.Equal(Base().SignedBytes(), reordered.SignedBytes());
        Assert.Equal(Base(), reordered);
    }

    [Fact]
    public void Create_rejects_no_key_ids()
    {
        Assert.Throws<ArgumentException>(() => ErasureProof.Create(
            Ref(1), [], ["gc-messages"], _backupExpiry, _erasedAt, "identity-key-1"));
    }

    [Fact]
    public void Create_rejects_an_empty_key_id()
    {
        Assert.Throws<ArgumentException>(() => ErasureProof.Create(
            Ref(1), ["key-a", ""], ["gc-messages"], _backupExpiry, _erasedAt, "identity-key-1"));
    }

    [Fact]
    public void Create_rejects_duplicate_key_ids()
    {
        Assert.Throws<ArgumentException>(() => ErasureProof.Create(
            Ref(1), ["key-a", "key-b", "key-a"], ["gc-messages"], _backupExpiry, _erasedAt, "identity-key-1"));
    }

    [Fact]
    public void Create_rejects_no_scopes()
    {
        Assert.Throws<ArgumentException>(() => ErasureProof.Create(
            Ref(1), ["key-a"], [], _backupExpiry, _erasedAt, "identity-key-1"));
    }

    [Fact]
    public void Create_rejects_duplicate_scopes()
    {
        Assert.Throws<ArgumentException>(() => ErasureProof.Create(
            Ref(1), ["key-a"], ["gc-messages", "gc-messages"], _backupExpiry, _erasedAt, "identity-key-1"));
    }

    [Fact]
    public void Create_rejects_an_empty_subject_reference()
    {
        Assert.Throws<ArgumentException>(() => ErasureProof.Create(
            [], ["key-a"], ["gc-messages"], _backupExpiry, _erasedAt, "identity-key-1"));
    }

    [Fact]
    public void Create_rejects_a_non_utc_erasure_time()
    {
        var local = _erasedAt.ToOffset(TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => ErasureProof.Create(
            Ref(1), ["key-a"], ["gc-messages"], _backupExpiry, local, "identity-key-1"));
    }

    [Fact]
    public void Create_rejects_a_non_utc_backup_expiry()
    {
        var local = _backupExpiry.ToOffset(TimeSpan.FromHours(-5));

        Assert.Throws<ArgumentException>(() => ErasureProof.Create(
            Ref(1), ["key-a"], ["gc-messages"], local, _erasedAt, "identity-key-1"));
    }

    [Fact]
    public void Create_rejects_a_backup_expiry_earlier_than_the_erasure()
    {
        Assert.Throws<ArgumentException>(() => ErasureProof.Create(
            Ref(1), ["key-a"], ["gc-messages"], _erasedAt.AddDays(-1), _erasedAt, "identity-key-1"));
    }

    [Fact]
    public void Create_accepts_a_backup_expiry_equal_to_the_erasure()
    {
        var proof = ErasureProof.Create(Ref(1), ["key-a"], ["gc-messages"], _erasedAt, _erasedAt, "identity-key-1");

        Assert.Equal(_erasedAt, proof.BackupExpiry);
    }

    [Fact]
    public void Create_rejects_an_empty_signing_key_id()
    {
        Assert.Throws<ArgumentException>(() => ErasureProof.Create(
            Ref(1), ["key-a"], ["gc-messages"], _backupExpiry, _erasedAt, " "));
    }

    [Fact]
    public void Signed_bytes_are_identical_for_the_same_proof()
    {
        Assert.Equal(Base().SignedBytes(), Base().SignedBytes());
    }

    [Fact]
    public void Signed_bytes_start_with_the_domain_tag()
    {
        var bytes = Base().SignedBytes();

        var tag = System.Text.Encoding.UTF8.GetBytes(ErasureProof.SignedDomain);
        Assert.Equal(tag.Length, (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        Assert.Equal(tag, bytes[4..(4 + tag.Length)]);
        Assert.Equal("BZ-ERASE-PROOF-v1", ErasureProof.SignedDomain);
    }

    [Fact]
    public void Signed_bytes_change_when_any_field_changes()
    {
        var baseline = Base().SignedBytes();

        var variants = new[]
        {
            ErasureProof.Create(Ref(2), ["key-a", "key-b"], ["gc-messages", "personal-fields"], _backupExpiry, _erasedAt, "identity-key-1"),
            ErasureProof.Create(Ref(1), ["key-a", "key-c"], ["gc-messages", "personal-fields"], _backupExpiry, _erasedAt, "identity-key-1"),
            ErasureProof.Create(Ref(1), ["key-a", "key-b"], ["gc-messages", "other"], _backupExpiry, _erasedAt, "identity-key-1"),
            ErasureProof.Create(Ref(1), ["key-a", "key-b"], ["gc-messages", "personal-fields"], _backupExpiry.AddDays(1), _erasedAt, "identity-key-1"),
            ErasureProof.Create(Ref(1), ["key-a", "key-b"], ["gc-messages", "personal-fields"], _backupExpiry, _erasedAt.AddMilliseconds(1), "identity-key-1"),
            ErasureProof.Create(Ref(1), ["key-a", "key-b"], ["gc-messages", "personal-fields"], _backupExpiry, _erasedAt, "identity-key-2"),
        };

        foreach (var variant in variants)
        {
            Assert.NotEqual(baseline, variant.SignedBytes());
        }
    }

    [Fact]
    public void Signed_bytes_do_not_merge_adjacent_list_entries()
    {
        // Length prefixes and list counts keep ["ab", "c"] apart from ["a", "bc"].
        var first = ErasureProof.Create(Ref(1), ["ab", "c"], ["s"], _backupExpiry, _erasedAt, "k");
        var second = ErasureProof.Create(Ref(1), ["a", "bc"], ["s"], _backupExpiry, _erasedAt, "k");

        Assert.NotEqual(first.SignedBytes(), second.SignedBytes());
    }

    [Fact]
    public void Equal_proofs_compare_equal_by_content()
    {
        Assert.Equal(Base(), Base());
        Assert.Equal(Base().GetHashCode(), Base().GetHashCode());
        var other = ErasureProof.Create(Ref(9), ["key-a"], ["s"], _backupExpiry, _erasedAt, "k");

        Assert.NotEqual(Base(), other);
    }
}
