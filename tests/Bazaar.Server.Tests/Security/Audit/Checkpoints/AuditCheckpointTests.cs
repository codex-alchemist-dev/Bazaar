using System.Security.Cryptography;
using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Tests.Security.Audit.Checkpoints;

public class AuditCheckpointTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    private static byte[] Hash(int n) => SHA256.HashData(BitConverter.GetBytes(n));

    private static AuditCheckpoint Base() => new(10, 19, 10, Hash(1), Hash(2), _at);

    [Fact]
    public void Signed_bytes_are_stable_for_equal_checkpoints()
    {
        Assert.Equal(Base().SignedBytes(), Base().SignedBytes());
    }

    [Fact]
    public void Signed_bytes_start_with_the_domain_tag()
    {
        var bytes = Base().SignedBytes();

        var tag = System.Text.Encoding.UTF8.GetBytes(AuditCheckpoint.SignedDomain);
        Assert.Equal(tag.Length, (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        Assert.Equal(tag, bytes[4..(4 + tag.Length)]);
    }

    [Fact]
    public void Signed_bytes_change_when_any_field_changes()
    {
        var baseline = Base().SignedBytes();

        var variants = new[]
        {
            Base() with { FirstSeq = 11 },
            Base() with { LastSeq = 20 },
            Base() with { RowCount = 11 },
            Base() with { ChainHeadHash = Hash(3) },
            Base() with { MerkleRoot = Hash(4) },
            Base() with { CreatedAt = _at.AddMilliseconds(1) },
        };

        foreach (var variant in variants)
        {
            Assert.NotEqual(baseline, variant.SignedBytes());
        }
    }

    [Fact]
    public void Signed_bytes_change_when_a_hash_byte_changes()
    {
        var head = Hash(1);
        var changed = (byte[])head.Clone();
        changed[31] ^= 0x80;

        Assert.NotEqual(Base().SignedBytes(), (Base() with { ChainHeadHash = changed }).SignedBytes());
    }

    [Fact]
    public void Field_boundaries_are_length_prefixed()
    {
        // Moving a byte from one hash to the other must not produce the same encoding.
        var a = new AuditCheckpoint(1, 1, 1, [1, 2], [3, 4], _at);
        var b = new AuditCheckpoint(1, 1, 1, [1], [2, 3, 4], _at);

        Assert.NotEqual(a.SignedBytes(), b.SignedBytes());
    }

    [Fact]
    public void Equality_compares_byte_arrays_by_content()
    {
        var copy = new AuditCheckpoint(10, 19, 10, (byte[])Hash(1).Clone(), (byte[])Hash(2).Clone(), _at);

        Assert.Equal(Base(), copy);
        Assert.Equal(Base().GetHashCode(), copy.GetHashCode());
        Assert.NotEqual(Base(), Base() with { MerkleRoot = Hash(9) });
        Assert.NotEqual(Base(), Base() with { CreatedAt = _at.AddTicks(1) });
    }

    [Fact]
    public void Equality_ignores_the_offset_of_the_timestamp()
    {
        var other = Base() with { CreatedAt = _at.ToOffset(TimeSpan.FromHours(2)) };

        Assert.Equal(Base(), other);
        Assert.Equal(Base().SignedBytes(), other.SignedBytes());
    }

    [Fact]
    public void Fake_signer_round_trips_and_rejects_a_changed_checkpoint()
    {
        var signer = new HmacTestSigner();
        var checkpoint = Base();
        var signature = signer.Sign(checkpoint.SignedBytes());

        Assert.True(signer.Verify(checkpoint.SignedBytes(), signature));
        Assert.False(signer.Verify((Base() with { RowCount = 11 }).SignedBytes(), signature));
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
