using System.Buffers.Binary;
using System.Text;

namespace Bazaar.Server.Security.Audit.Checkpoints;

/// <summary>
/// A signed checkpoint over the audit chain (design doc 48, section 5.2): the chain head, the row count, and a Merkle
/// root over the rows since the previous checkpoint. The audit signing key signs <see cref="SignedBytes"/>; the key is
/// held by the Identity Service and is never readable by agents, so this record only defines the bytes that get signed.
/// </summary>
/// <remarks>
/// Byte arrays are compared with <see cref="System.MemoryExtensions.SequenceEqual{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>,
/// so two checkpoints are equal when their values are equal, not when their array references are.
/// </remarks>
public sealed record AuditCheckpoint(
    long FirstSeq,
    long LastSeq,
    long RowCount,
    byte[] ChainHeadHash,
    byte[] MerkleRoot,
    DateTimeOffset CreatedAt)
{
    /// <summary>Format tag at the start of the canonical encoding. Changing the encoding means a new tag.</summary>
    public const string SignedDomain = "bazaar.audit.checkpoint.v1";

    /// <summary>
    /// Canonical bytes that the signer signs: a length-prefixed domain tag, then the fields in declaration order.
    /// Integers are big-endian 64-bit, the timestamp is UTC ticks, and byte arrays and strings carry a 32-bit length.
    /// Any change to any field changes these bytes.
    /// </summary>
    public byte[] SignedBytes()
    {
        ArgumentNullException.ThrowIfNull(ChainHeadHash);
        ArgumentNullException.ThrowIfNull(MerkleRoot);

        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[8];

        WriteField(stream, Encoding.UTF8.GetBytes(SignedDomain));
        BinaryPrimitives.WriteInt64BigEndian(number, FirstSeq);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, LastSeq);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, RowCount);
        stream.Write(number);
        WriteField(stream, ChainHeadHash);
        WriteField(stream, MerkleRoot);
        BinaryPrimitives.WriteInt64BigEndian(number, CreatedAt.ToUniversalTime().UtcTicks);
        stream.Write(number);

        return stream.ToArray();
    }

    /// <summary>Value equality over the fields; byte arrays are compared by content.</summary>
    public bool Equals(AuditCheckpoint? other) =>
        other is not null
        && FirstSeq == other.FirstSeq
        && LastSeq == other.LastSeq
        && RowCount == other.RowCount
        && ChainHeadHash.AsSpan().SequenceEqual(other.ChainHeadHash)
        && MerkleRoot.AsSpan().SequenceEqual(other.MerkleRoot)
        && CreatedAt.ToUniversalTime().UtcTicks == other.CreatedAt.ToUniversalTime().UtcTicks;

    /// <summary>Hash code consistent with <see cref="Equals(AuditCheckpoint?)"/>.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FirstSeq);
        hash.Add(LastSeq);
        hash.Add(RowCount);
        hash.AddBytes(ChainHeadHash);
        hash.AddBytes(MerkleRoot);
        hash.Add(CreatedAt.ToUniversalTime().UtcTicks);
        return hash.ToHashCode();
    }

    private static void WriteField(MemoryStream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}
